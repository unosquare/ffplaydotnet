namespace NAudio.Wave
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Runtime.InteropServices;
    using System.Linq;

    /// <summary>
    /// Alternative WaveOut class, making use of the Event callback
    /// </summary>
    public class WaveOutEvent
    {
        #region State Variables

        private readonly object WaveOutLock = new object();
        private readonly SynchronizationContext SyncContext;
        private IntPtr DeviceHandle; // WaveOut handle
        private WaveOutBuffer[] Buffers;
        private IWaveProvider WaveStream;
        private volatile PlaybackState playbackState;
        private AutoResetEvent callbackEvent;
        private int m_DeviceNumber = -1;

        #endregion

        #region Events

        /// <summary>
        /// Indicates playback has stopped automatically
        /// </summary>
        public event EventHandler<StoppedEventArgs> PlaybackStopped;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="WaveOutEvent"/> class.
        /// </summary>
        public WaveOutEvent()
        {
            SyncContext = SynchronizationContext.Current;
            if (SyncContext != null &&
                ((SyncContext.GetType().Name == "LegacyAspNetSynchronizationContext") ||
                (SyncContext.GetType().Name == "AspNetSynchronizationContext")))
            {
                SyncContext = null;
            }

            // set default values up
            DeviceNumber = 0;
            DesiredLatency = 300;
            NumberOfBuffers = 2;
        }


        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the desired latency in milliseconds
        /// Should be set before a call to Init
        /// </summary>
        public int DesiredLatency { get; set; }

        /// <summary>
        /// Gets or sets the number of buffers used
        /// Should be set before a call to Init
        /// </summary>
        public int NumberOfBuffers { get; set; }

        /// <summary>
        /// Gets or sets the device number
        /// Should be set before a call to Init
        /// This must be between -1 and <see>DeviceCount</see> - 1.
        /// -1 means stick to default device even default device is changed
        /// </summary>
        public int DeviceNumber
        {
            get { return m_DeviceNumber; }
            set
            {
                m_DeviceNumber = value;
                lock (WaveOutLock)
                {
                    WaveOutCapabilities caps;
                    WaveInterop.waveOutGetDevCaps((IntPtr)m_DeviceNumber, out caps, Marshal.SizeOf(typeof(WaveOutCapabilities)));
                    Capabilities = caps;
                }
            }
        }

        /// <summary>
        /// Gets a <see cref="Wave.WaveFormat"/> instance indicating the format the hardware is using.
        /// </summary>
        public WaveFormat OutputWaveFormat
        {
            get { return WaveStream.WaveFormat; }
        }

        /// <summary>
        /// Playback State
        /// </summary>
        public PlaybackState PlaybackState
        {
            get { return playbackState; }
        }

        /// <summary>
        /// Gets the capabilities.
        /// </summary>
        public WaveOutCapabilities Capabilities { get; private set; }

        #endregion

        #region Public API

        /// <summary>
        /// Initializes the specified wave provider.
        /// </summary>
        /// <param name="waveProvider">The wave provider.</param>
        /// <exception cref="System.InvalidOperationException">Can't re-initialize during playback</exception>
        public void Init(IWaveProvider waveProvider)
        {
            if (playbackState != PlaybackState.Stopped)
                throw new InvalidOperationException("Can't re-initialize during playback");

            if (DeviceHandle != IntPtr.Zero)
            {
                // normally we don't allow calling Init twice, but as experiment, see if we can clean up and go again
                // try to allow reuse of this waveOut device
                // n.b. risky if Playback thread has not exited
                DisposeBuffers();
                CloseWaveOut();
            }

            callbackEvent = new AutoResetEvent(false);

            WaveStream = waveProvider;
            var bufferSize = waveProvider.WaveFormat.ConvertLatencyToByteSize((DesiredLatency + NumberOfBuffers - 1) / NumberOfBuffers);

            MmResult result;
            lock (WaveOutLock)
            {
                result = WaveInterop.waveOutOpenWindow(out DeviceHandle, (IntPtr)DeviceNumber, WaveStream.WaveFormat,
                    callbackEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WaveInterop.WaveInOutOpenFlags.CallbackEvent);
            }

            MmException.Try(result, nameof(WaveInterop.waveOutOpen));

            Buffers = new WaveOutBuffer[NumberOfBuffers];
            playbackState = PlaybackState.Stopped;
            for (var n = 0; n < NumberOfBuffers; n++)
            {
                Buffers[n] = new WaveOutBuffer(DeviceHandle, bufferSize, WaveStream, WaveOutLock);
            }
        }

        /// <summary>
        /// Start playing the audio from the WaveStream
        /// </summary>
        public void Play()
        {
            if (Buffers == null || WaveStream == null)
            {
                throw new InvalidOperationException("Must call Init first");
            }
            if (playbackState == PlaybackState.Stopped)
            {
                playbackState = PlaybackState.Playing;
                callbackEvent.Set(); // give the thread a kick
                ThreadPool.QueueUserWorkItem(state => PlaybackThread(), null);
            }
            else if (playbackState == PlaybackState.Paused)
            {
                Resume();
                callbackEvent.Set(); // give the thread a kick
            }
        }

        /// <summary>
        /// Pause the audio
        /// </summary>
        public void Pause()
        {
            if (playbackState == PlaybackState.Playing)
            {
                MmResult result;
                playbackState = PlaybackState.Paused; // set this here to avoid a deadlock problem with some drivers
                lock (WaveOutLock)
                {
                    result = WaveInterop.waveOutPause(DeviceHandle);
                }
                if (result != MmResult.NoError)
                {
                    throw new MmException(result, "waveOutPause");
                }
            }
        }

        /// <summary>
        /// Resume playing after a pause from the same position
        /// </summary>
        private void Resume()
        {
            if (playbackState == PlaybackState.Paused)
            {
                MmResult result;
                lock (WaveOutLock)
                {
                    result = WaveInterop.waveOutRestart(DeviceHandle);
                }
                if (result != MmResult.NoError)
                {
                    throw new MmException(result, "waveOutRestart");
                }
                playbackState = PlaybackState.Playing;
            }
        }

        /// <summary>
        /// Stop and reset the WaveOut device
        /// </summary>
        public void Stop()
        {
            if (playbackState != PlaybackState.Stopped)
            {
                // in the call to waveOutReset with function callbacks
                // some drivers will block here until OnDone is called
                // for every buffer
                playbackState = PlaybackState.Stopped; // set this here to avoid a problem with some drivers whereby 
                MmResult result;
                lock (WaveOutLock)
                {
                    result = WaveInterop.waveOutReset(DeviceHandle);
                }
                if (result != MmResult.NoError)
                {
                    throw new MmException(result, "waveOutReset");
                }
                callbackEvent.Set(); // give the thread a kick, make sure we exit
            }
        }

        /// <summary>
        /// Gets the current position in bytes from the wave output device.
        /// (n.b. this is not the same thing as the position within your reader
        /// stream - it calls directly into waveOutGetPosition)
        /// </summary>
        /// <returns>Position in bytes</returns>
        public long GetPosition()
        {
            lock (WaveOutLock)
            {
                var mmTime = new MmTime();
                mmTime.wType = MmTime.TIME_BYTES; // request results in bytes, TODO: perhaps make this a little more flexible and support the other types?
                MmException.Try(WaveInterop.waveOutGetPosition(DeviceHandle, out mmTime, Marshal.SizeOf(mmTime)), "waveOutGetPosition");

                if (mmTime.wType != MmTime.TIME_BYTES)
                    throw new Exception(string.Format("waveOutGetPosition: wType -> Expected {0}, Received {1}", MmTime.TIME_BYTES, mmTime.wType));

                return mmTime.cb;
            }
        }

        #endregion

        private void PlaybackThread()
        {
            Exception exception = null;
            try
            {
                DoPlayback();
            }
            catch (Exception e)
            {
                exception = e;
            }
            finally
            {
                playbackState = PlaybackState.Stopped;
                // we're exiting our background thread
                RaisePlaybackStoppedEvent(exception);
            }
        }

        private void DoPlayback()
        {
            while (playbackState != PlaybackState.Stopped)
            {
                if (!callbackEvent.WaitOne(DesiredLatency))
                {
                    if (playbackState == PlaybackState.Playing)
                    {
                        Debug.WriteLine("WARNING: WaveOutEvent callback event timeout");
                    }
                }


                // requeue any buffers returned to us
                if (playbackState == PlaybackState.Playing)
                {
                    int queued = 0;
                    foreach (var buffer in Buffers)
                    {
                        if (buffer.InQueue || buffer.OnDone())
                        {
                            queued++;
                        }
                    }
                    if (queued == 0)
                    {
                        // we got to the end
                        playbackState = PlaybackState.Stopped;
                        callbackEvent.Set();
                    }
                }
            }
        }


        /// <summary>
        /// Raises the playback stopped event.
        /// </summary>
        /// <param name="e">The e.</param>
        private void RaisePlaybackStoppedEvent(Exception e)
        {
            var handler = PlaybackStopped;
            if (handler != null)
            {
                if (SyncContext == null)
                {
                    handler(this, new StoppedEventArgs(e));
                }
                else
                {
                    SyncContext.Post(state => handler(this, new StoppedEventArgs(e)), null);
                }
            }
        }

        #region IDispose Pattern

        /// <summary>
        /// Closes this WaveOut device
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        /// <summary>
        /// Closes the WaveOut device and disposes of buffers
        /// </summary>
        /// <param name="disposing">True if called from <see>Dispose</see></param>
        protected void Dispose(bool disposing)
        {
            Stop();

            if (disposing)
            {
                DisposeBuffers();
            }

            CloseWaveOut();
        }

        /// <summary>
        /// Closes the wave device.
        /// </summary>
        private void CloseWaveOut()
        {
            if (callbackEvent != null)
            {
                callbackEvent.Close();
                callbackEvent = null;
            }
            lock (WaveOutLock)
            {
                if (DeviceHandle != IntPtr.Zero)
                {
                    WaveInterop.waveOutClose(DeviceHandle);
                    DeviceHandle = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// Disposes the buffers.
        /// </summary>
        private void DisposeBuffers()
        {
            if (Buffers != null)
            {
                foreach (var buffer in Buffers)
                {
                    buffer.Dispose();
                }
                Buffers = null;
            }
        }

        /// <summary>
        /// Finalizer. Only called when user forgets to call <see>Dispose</see>
        /// </summary>
        ~WaveOutEvent()
        {
            Dispose(false);
            Debug.Assert(false, "WaveOutEvent device was not closed");
        }

        #endregion

    }
}