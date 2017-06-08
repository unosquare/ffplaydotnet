namespace Unosquare.FFplayDotNet.Rendering
{
    using NAudio.Wave;
    using System;
    using System.Windows;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.FFplayDotNet.Decoding;

    /// <summary>
    /// Provides Audio Output capabilities
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    internal sealed class AudioRenderer : IDisposable, IRenderer, IWaveProvider
    {

        #region Private Members

        private readonly MediaElement MediaElement;
        private WaveOutEvent AudioDevice;
        private CircularBuffer AudioBuffer;
        private bool IsDisposed = false;

        private WaveFormat m_Format = null;
        private byte[] SilenceBuffer = null;
        private byte[] ReadBuffer = null;

        private double m_Volume = 1.0d;
        private double m_Balance = 0.0d;
        private double m_LeftVolume = 1.0d;
        private double m_RightVolume = 1.0d;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioRenderer"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public AudioRenderer(MediaElement mediaElement)
        {
            MediaElement = mediaElement;

            m_Format = new WaveFormat(AudioParams.Output.SampleRate, AudioParams.OutputBitsPerSample, AudioParams.Output.ChannelCount);
            if (WaveFormat.BitsPerSample != 16 || WaveFormat.Channels != 2)
                throw new NotSupportedException("Wave Format has to be 16-bit and 2-channel.");

            SilenceBuffer = new byte[m_Format.BitsPerSample / 8 * m_Format.Channels * 2];

            if (MediaElement.HasAudio)
                Initialize();

            if (Application.Current != null)
                Application.Current.Exit += (s, e) =>
                {
                    Destroy();
                };
        }

        #endregion

        #region Initialization and Destruction

        /// <summary>
        /// Initializes the audio renderer.
        /// Call the Play Method to start reading samples
        /// </summary>
        private void Initialize()
        {
            Destroy();

            AudioDevice = new WaveOutEvent()
            {
                DesiredLatency = 200,
                NumberOfBuffers = 2,
            };

            var bufferLength = WaveFormat.ConvertLatencyToByteSize(AudioDevice.DesiredLatency) * MediaElement.Blocks[MediaType.Audio].Capacity / 2;
            AudioBuffer = new CircularBuffer(bufferLength);
            AudioDevice.Init(this);
        }


        /// <summary>
        /// Destroys the audio renderer.
        /// Makes it useless.
        /// </summary>
        private void Destroy()
        {
            if (AudioDevice != null)
            {
                AudioDevice.Stop();
                AudioDevice.Dispose();
                AudioDevice = null;
            }

            if (AudioBuffer != null)
            {
                AudioBuffer.Dispose();
                AudioBuffer = null;
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the WaveFormat of this WaveProvider.
        /// </summary>
        /// <value>
        /// The wave format.
        /// </value>
        public WaveFormat WaveFormat
        {
            get { return m_Format; }
        }


        /// <summary>
        /// Gets or sets the volume.
        /// </summary>
        /// <value>
        /// The volume.
        /// </value>
        public double Volume
        {
            get { return m_Volume; }
            set
            {
                if (value < 0) value = 0;
                if (value > 1) value = 1;

                var leftFactor = m_Balance > 0 ? 1d - m_Balance : 1d;
                var rightFactor = m_Balance < 0 ? 1d + m_Balance : 1d;

                m_LeftVolume = leftFactor * value;
                m_RightVolume = rightFactor * value;
                m_Volume = value;
            }
        }

        /// <summary>
        /// Gets or sets the balance (-1 to 1).
        /// </summary>
        public double Balance
        {
            get { return m_Balance; }
            set
            {
                if (value < -1) value = -1;
                if (value > 1) value = 1;
                m_Balance = value;
                Volume = m_Volume;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Renders the specified media block.
        /// </summary>
        /// <param name="mediaBlock">The media block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <param name="renderIndex">Index of the render.</param>
        public void Render(MediaBlock mediaBlock, TimeSpan clockPosition, int renderIndex)
        {
            if (AudioBuffer == null) return;
            var block = mediaBlock as AudioBlock;
            if (block == null) return;

            var currentIndex = renderIndex;
            var audioBlocks = MediaElement.Blocks[MediaType.Audio];
            var addedBlockCount = 0;
            var addedBytes = 0;
            while (currentIndex >= 0 && currentIndex < audioBlocks.Count)
            {
                var audioBlock = audioBlocks[currentIndex] as AudioBlock;
                if (AudioBuffer.WriteTag < audioBlock.StartTime)
                {
                    AudioBuffer.Write(audioBlock.Buffer, audioBlock.BufferLength, audioBlock.StartTime);
                    addedBlockCount++;
                    addedBytes += audioBlock.BufferLength;
                }

                currentIndex++;

                // Stop adding if we have too much in there.
                if (AudioBuffer.CapacityPercent >= 0.8)
                    break;
            }
        }

        /// <summary>
        /// Executed when the Play method is called on the parent MediaElement
        /// </summary>
        public void Play()
        {
            AudioDevice?.Play();
        }

        /// <summary>
        /// Executed when the Pause method is called on the parent MediaElement
        /// </summary>
        public void Pause()
        {
            //AudioDevice?.Pause();
        }

        /// <summary>
        /// Executed when the Pause method is called on the parent MediaElement
        /// </summary>
        public void Stop()
        {
            //AudioDevice?.Stop();
            AudioBuffer.Clear();
        }

        /// <summary>
        /// Executed when the Close method is called on the parent MediaElement
        /// </summary>
        public void Close()
        {
            Destroy();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Called whenever the audio driver requests samples
        /// </summary>
        /// <param name="renderBuffer">The render buffer.</param>
        /// <param name="renderBufferOffset">The render buffer offset.</param>
        /// <param name="requestedBytes">The requested bytes.</param>
        /// <returns></returns>
        public int Read(byte[] renderBuffer, int renderBufferOffset, int requestedBytes)
        {
            if (MediaElement.IsPlaying == false || MediaElement.HasAudio == false || AudioBuffer.ReadableCount < requestedBytes)
            {
                Buffer.BlockCopy(SilenceBuffer, 0, renderBuffer, renderBufferOffset, Math.Min(SilenceBuffer.Length, renderBuffer.Length));
                return SilenceBuffer.Length;
            }

            if (ReadBuffer == null || ReadBuffer.Length != requestedBytes)
                ReadBuffer = new byte[requestedBytes];

            AudioBuffer.Read(requestedBytes, ReadBuffer);

            var isLeftSample = true;
            for (var baseIndex = 0; baseIndex < ReadBuffer.Length; baseIndex += WaveFormat.BitsPerSample / 8)
            {

                var sample = BitConverter.ToInt16(ReadBuffer, baseIndex);

                if (isLeftSample && m_LeftVolume != 1.0)
                    sample = (short)(sample * m_LeftVolume);
                else if (isLeftSample == false && m_RightVolume != 1.0)
                    sample = (short)(sample * m_RightVolume);

                renderBuffer[baseIndex] = (byte)(sample & 0xff);
                renderBuffer[baseIndex + 1] = (byte)(sample >> 8);
            }

            return requestedBytes;
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">
        ///   <c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                    Destroy();

                IsDisposed = true;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

    }
}
