namespace Unosquare.FFplayDotNet
{
    using Core;
    using Decoding;
    using Swan;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Controls;

    public class PlaybackController : INotifyPropertyChanged
    {
        // TODO: implement IDisposable to dispose of blocks and frames upon closing.

        #region Constants

        private static readonly int StateDictionaryCapacity = Constants.MediaTypes.Count - 1;
        private const int MaxPacketBufferLength = 1024 * 1024 * 8; // 8MB buffer
        private const int PacketReadBatchCount = 10; // Read 10 packets at a time

        private static readonly Dictionary<MediaType, int> MaxBlocks
            = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, 12 },
            { MediaType.Audio, 24 },
            { MediaType.Subtitle, 48 }
        };

        private static readonly Dictionary<MediaType, int> MaxFrames
            = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, 24 },
            { MediaType.Audio, 48 },
            { MediaType.Subtitle, 48 }
        };

        #endregion

        #region Events

        // TODO: implement these event handlers
        public event EventHandler BufferingEnded;
        public event EventHandler BufferingStarted;

        public event EventHandler<MediaOpeningEventArgs> MediaOpening;
        public event EventHandler MediaOpened;
        public event EventHandler<MediaFailedEventArgs> MediaFailed;
        public event EventHandler<MediaBlockAvailableEventArgs> MediaBlockAvailable;
        public event EventHandler MediaEnded;

        #endregion

        #region INotifyPropertyChanged Implementation

        /// <summary>
        /// Multicast event for property change notifications.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Checks if a property already matches a desired value.  Sets the property and
        /// notifies listeners only when necessary.
        /// </summary>
        /// <typeparam name="T">Type of the property.</typeparam>
        /// <param name="storage">Reference to a property with both getter and setter.</param>
        /// <param name="value">Desired value for the property.</param>
        /// <param name="propertyName">Name of the property used to notify listeners.  This
        /// value is optional and can be provided automatically when invoked from compilers that
        /// support CallerMemberName.</param>
        /// <returns>True if the value was changed, false if the existing value matched the
        /// desired value.</returns>
        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Notifies listeners that a property value has changed.
        /// </summary>
        /// <param name="propertyName">Name of the property used to notify listeners.  This
        /// value is optional and can be provided automatically when invoked from compilers
        /// that support <see cref="CallerMemberNameAttribute"/>.</param>
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Private Members

        private readonly Clock Clock = new Clock();

        private readonly Dictionary<MediaType, MediaFrameQueue> Frames
            = new Dictionary<MediaType, MediaFrameQueue>(StateDictionaryCapacity);

        private readonly Dictionary<MediaType, MediaBlockBuffer> Blocks
            = new Dictionary<MediaType, MediaBlockBuffer>(StateDictionaryCapacity);

        private readonly Dictionary<MediaType, TimeSpan> LastRenderTime
            = new Dictionary<MediaType, TimeSpan>(StateDictionaryCapacity);

        private Task PacketReadingTask;
        private readonly CancellationTokenSource PacketReadingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim PacketReadingCycle = new ManualResetEventSlim(false);

        private Task FrameDecodingTask;
        private readonly CancellationTokenSource FrameDecodingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim FrameDecodingCycle = new ManualResetEventSlim(false);

        private Task BlockRenderingTask;
        private readonly CancellationTokenSource BlockRenderingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim BlockRenderingCycle = new ManualResetEventSlim(false);

        private readonly ManualResetEventSlim SeekingDone = new ManualResetEventSlim(true);

        private MediaState m_MediaState = MediaState.Close;

        private TimeSpan? RequestedSeekPosition = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackController" /> class.
        /// </summary>
        public PlaybackController()
        {
            // placeholder
        }

        #endregion

        #region Properties

        /// <summary>
        /// Provides access to the undelying media container.
        /// </summary>
        internal MediaContainer Container { get; private set; }

        /// <summary>
        /// Gets a value indicating whether more packets can be read from the stream.
        /// This does not check if the packet queue is full.
        /// </summary>
        private bool CanReadMorePackets { get { return Container.IsAtEndOfStream == false; } }

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded from the packet queue.
        /// That is, if we have packets in the packet buffer or if we are not at the end of the stream.
        /// </summary>
        private bool CanReadMoreFrames { get { return Container.Components.PacketBufferLength > 0 || CanReadMorePackets; } }

        /// <summary>
        /// Gets a value indicating whether more frames can be converted into blocks.
        /// </summary>
        private bool CanReadMoreBlocks { get { return Frames.Any(f => f.Value.Count > 0) || CanReadMoreFrames || CanReadMorePackets; } }

        #endregion

        #region Methods

        /// <summary>
        /// Gets a value indicating whether more frames can be converted into blocks of the given type.
        /// </summary>
        private bool CanReadMoreBlocksOf(MediaType t) { return Frames[t].Count > 0 || CanReadMoreFrames || CanReadMorePackets; }

        /// <summary>
        /// Dequeues the next available frame and converts it into a block of the appropriate type,
        /// adding it to the correpsonding block buffer. If there is no more blocks in the pool, then 
        /// more room is provided automatically.
        /// </summary>
        /// <param name="t">The media type.</param>
        private void AddNextBlock(MediaType t)
        {
            var frame = Frames[t].Dequeue();
            if (frame == null) return;
            Blocks[t].Add(frame, Container);
        }

        /// <summary>
        /// Buffers some packets which in turn get decoded into frames and then
        /// converted into blocks.
        /// </summary>
        /// <param name="packetBufferLength">Length of the packet buffer.</param>
        /// <param name="setClock">if set to <c>true</c> [set clock].</param>
        private void BufferBlocks(int packetBufferLength, bool setClock)
        {
            // Raise the buffering started event.
            BufferingStarted?.Invoke(this, EventArgs.Empty);

            // Pause the clock while we buffer blocks
            var wasClockRunning = Clock.IsRunning;
            if (setClock) Clock.Pause();

            // Buffer some packets
            while (CanReadMorePackets && Container.Components.PacketBufferLength < packetBufferLength)
                PacketReadingCycle.Wait(1);

            // Wait up to 1 second to decode frames. This happens much faster but 1s is plenty.
            FrameDecodingCycle.Wait(1000);

            // Buffer some blocks
            while (CanReadMoreBlocks && Blocks.All(b => b.Value.CapacityPercent < 0.5d))
            {
                PacketReadingCycle.Wait(1);
                FrameDecodingCycle.Wait(1);
                foreach (var t in Container.Components.MediaTypes)
                    AddNextBlock(t);
            }

            // Resume and set the clock if requested.
            if (setClock)
            {
                Clock.Position = Blocks[Container.Components.Main.MediaType].RangeStartTime;
                if (wasClockRunning) Clock.Play();
            }

            // Raise the buffering started event.
            BufferingEnded?.Invoke(this, EventArgs.Empty);

        }

        /// <summary>
        /// The render block callback that updates the reported media position
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <param name="renderIndex">Index of the render.</param>
        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            MediaBlockAvailable?.Invoke(this, new MediaBlockAvailableEventArgs(block, clockPosition));
            return;

            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            ($"{block.MediaType.ToString().Substring(0, 1)} "
                + $"BLK: {block.StartTime.Debug()} | "
                + $"CLK: {clockPosition.Debug()} | "
                + $"DFT: {drift.TotalMilliseconds,4:0} | "
                + $"IX: {renderIndex,3} | "
                + $"FQ: {Frames[block.MediaType].Count,4} | "
                + $"PQ: {Container.Components[block.MediaType].PacketBufferLength / 1024d,7:0.0}k | "
                + $"TQ: {Container.Components.PacketBufferLength / 1024d,7:0.0}k").Info(typeof(MediaContainer));
        }

        /// <summary>
        /// Performs a seek operation to the specified position.
        /// </summary>
        /// <param name="position">The position.</param>
        private void Seek(TimeSpan position)
        {
            SeekingDone.Wait();
            var startTime = DateTime.UtcNow;
            var resumeClock = Clock.IsRunning;
            Clock.Pause();

            SeekingDone.Reset();
            PacketReadingCycle.Wait();
            FrameDecodingCycle.Wait();
            BlockRenderingCycle.Wait();

            // Clear Blocks and frames, reset the render times
            foreach (var t in Container.Components.MediaTypes)
            {
                Frames[t].Clear();
                Blocks[t].Clear();
                LastRenderTime[t] = TimeSpan.MinValue;
            }

            // Populate frame with after-seek operation
            var frames = Container.Seek(position);
            foreach (var frame in frames)
                Frames[frame.MediaType].Push(frame);

            // Resume the clock if it was running before the seek operation
            OnPropertyChanged(nameof(Position));
            if (resumeClock)
                Clock.Play();

            $"SEEK D: Elapsed: {startTime.DebugElapsedUtc()}".Debug(typeof(MediaContainer));

            RequestedSeekPosition = null;
            SeekingDone.Set();
        }

        #endregion

        #region Public API

        public TimeSpan Position
        {
            get { return Clock.Position; }
            set { RequestedSeekPosition = value; } // TODO: this is not ready yet. It needs a lot more logic.
        }

        public double SpeedRatio
        {
            get { return Clock.SpeedRatio; }
            set { Clock.SpeedRatio = value; OnPropertyChanged(); }
        }

        public MediaState State
        {
            get { return m_MediaState; }
            private set { SetProperty(ref m_MediaState, value); }
        }

        public void Open(string mediaUrl)
        {
            try
            {
                Container = new MediaContainer(mediaUrl);
                MediaOpening?.Invoke(this, new MediaOpeningEventArgs(Container.MediaOptions));
                Container.Initialize();

                foreach (var t in Container.Components.MediaTypes)
                {
                    Blocks[t] = new MediaBlockBuffer(MaxBlocks[t], t);
                    Frames[t] = new MediaFrameQueue();
                    LastRenderTime[t] = TimeSpan.MinValue;
                }

                PacketReadingTask = Task.Run(() => { RunPacketReadingTask(); }, PacketReadingCancel.Token);
                FrameDecodingTask = Task.Run(() => { RunFrameDecodingTask(); }, FrameDecodingCancel.Token);
                BlockRenderingTask = Task.Run(() => { RunBlockRenderingTask(); }, BlockRenderingCancel.Token);
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MediaFailed?.Invoke(this, new MediaFailedEventArgs(ex));
            }

        }

        public void Play()
        {
            Clock.Play();
            BlockRenderingCycle.Wait(5);
            State = MediaState.Play;
        }

        public void Pause()
        {
            Clock.Pause();
            BlockRenderingCycle.Wait(5);
            State = MediaState.Pause;
        }

        public void Stop()
        {
            Clock.Pause();
            RequestedSeekPosition = TimeSpan.Zero;
            BlockRenderingCycle.Wait(5);
            State = MediaState.Stop;
        }

        public void Close()
        {
            throw new NotImplementedException();
            //BlockRenderingTask.Wait();
            //State = MediaState.Close;
        }

        #endregion

        #region Task Runners

        /// <summary>
        /// Runs the read task which keeps a packet buffer as full as possible.
        /// </summary>
        private void RunPacketReadingTask()
        {
            var packetsRead = 0;

            while (!PacketReadingCancel.IsCancellationRequested)
            {
                // Enter a read cycle
                SeekingDone.Wait();
                PacketReadingCycle.Reset();

                // Read a bunch of packets at a time
                packetsRead = 0;
                while (CanReadMorePackets
                    && packetsRead < PacketReadBatchCount
                    && Container.Components.PacketBufferLength < MaxPacketBufferLength)
                {
                    Container.Read();
                    packetsRead++;
                }

                PacketReadingCycle.Set();

                if (!CanReadMorePackets || Container.Components.PacketBufferLength > MaxPacketBufferLength)
                    Thread.Sleep(1);

            }

            PacketReadingCycle.Set();
        }

        /// <summary>
        /// Continually decodes the available packet buffer to have as
        /// many frames as possible in each frame queue and 
        /// up to the MaxFrames on each component
        /// </summary>
        private void RunFrameDecodingTask()
        {
            while (FrameDecodingCancel.IsCancellationRequested == false)
            {
                // Wait for a seek operation to complete (if any)
                // and initiate a decoding cycle.
                SeekingDone.Wait();
                FrameDecodingCycle.Reset();

                // Decode Frames if necessary
                var decodedFrames = 0;

                // Decode frames for each of the components
                foreach (var component in Container.Components.All)
                {
                    // Check if we can accept more frames
                    if (Frames[component.MediaType].Count >= MaxFrames[component.MediaType])
                        continue;

                    // Don't do anything if we don't have packets to decode
                    if (component.PacketBufferCount <= 0)
                        continue;

                    // Push the decoded frames
                    var frames = component.DecodeNextPacket();
                    foreach (var frame in frames)
                    {
                        Frames[frame.MediaType].Push(frame);
                        decodedFrames += 1;
                    }
                }

                // Complete the frame decoding cycle
                FrameDecodingCycle.Set();

                // Give it a break if there wa snothing to decode.
                if (decodedFrames <= 0)
                    Thread.Sleep(1);

            }

            FrameDecodingCycle.Set();
        }

        /// <summary>
        /// Continuously converts frmes and places them on the corresponding
        /// block buffer. This task is responsible for keeping track of the clock
        /// and calling the render methods appropriate for the current clock position.
        /// </summary>
        /// <returns></returns>
        private void RunBlockRenderingTask()
        {
            var mediaTypeCount = Container.Components.MediaTypes.Length;
            var main = Container.Components.Main.MediaType;

            var clockPosition = Clock.Position;

            var hasRendered = new Dictionary<MediaType, bool>(mediaTypeCount);
            var renderIndex = new Dictionary<MediaType, int>(mediaTypeCount);
            var renderBlock = new Dictionary<MediaType, MediaBlock>(mediaTypeCount);

            foreach (var t in Container.Components.MediaTypes)
            {
                hasRendered[t] = false;
                renderIndex[t] = -1;
                renderBlock[t] = null;
            }

            // Buffer some blocks
            BufferBlocks(MaxPacketBufferLength / 8, true);

            while (BlockRenderingCancel.IsCancellationRequested == false)
            {
                if (RequestedSeekPosition != null)
                    Seek(RequestedSeekPosition.Value);

                SeekingDone.Wait();
                BlockRenderingCycle.Reset();

                // Capture current time and render index
                OnPropertyChanged(nameof(Position));
                clockPosition = Clock.Position;
                renderIndex[main] = Blocks[main].IndexOf(clockPosition);

                // Check for out-of sync issues (i.e. after seeking)
                if (Blocks[main].IsInRange(clockPosition) == false || renderIndex[main] < 0)
                {
                    BufferBlocks(MaxPacketBufferLength / 4, true);
                    $"SYNC              CLK: {clockPosition.Debug()} | TGT: {Blocks[main].RangeStartTime.Debug()}".Warn(typeof(MediaContainer));
                    clockPosition = Clock.Position;
                    renderIndex[main] = Blocks[main].IndexOf(clockPosition);
                }

                foreach (var t in Container.Components.MediaTypes)
                {
                    var blocks = Blocks[t];
                    renderIndex[t] = blocks.IndexOf(clockPosition);
                    if (renderIndex[t] < 0)
                        continue;

                    // Retrieve the render block
                    renderBlock[t] = blocks[renderIndex[t]];
                    hasRendered[t] = false;

                    // render the frame if we have not rendered
                    if (renderBlock[t].StartTime != LastRenderTime[t]
                        && renderBlock[t].StartTime.Ticks <= clockPosition.Ticks)
                    {
                        LastRenderTime[t] = renderBlock[t].StartTime;
                        hasRendered[t] = true;
                        RenderBlock(renderBlock[t], clockPosition, renderIndex[t]);
                    }

                    // Add the next block if the conditions require us to do so:
                    // If rendered, then we need to discard the oldest and add the newest
                    // If the render index is greater than half, the capacity, add a new block
                    while (hasRendered[t] || renderIndex[t] + 1 > Blocks[t].Capacity / 2)
                    {
                        AddNextBlock(t);
                        hasRendered[t] = false;
                        renderIndex[t] = Blocks[t].IndexOf(clockPosition);

                        // Stop the loop if we can't reach the conditions.
                        if (Frames[t].Count == 0)
                            break;
                    }
                }

                // Detect end of block rendering
                if (CanReadMoreBlocksOf(main) == false && renderIndex[main] == Blocks[main].Count - 1)
                {
                    if (State != MediaState.Pause)
                    {
                        // Rendered all and nothing else to read
                        Clock.Pause();
                        Clock.Position = Blocks[main].RangeEndTime;
                        State = MediaState.Pause;
                        OnPropertyChanged(nameof(Position));
                        MediaEnded?.Invoke(this, EventArgs.Empty);
                    }

                }

                BlockRenderingCycle.Set();
                Thread.Sleep(1);
            }

            BlockRenderingCycle.Set();
        }

        #endregion
    }
}
