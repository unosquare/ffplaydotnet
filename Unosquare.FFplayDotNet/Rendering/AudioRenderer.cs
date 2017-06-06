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
    internal sealed class AudioRenderer : IDisposable, IRenderer
    {

        #region Private Members

        private readonly MediaElement MediaElement;
        private WaveOutEvent AudioDevice;
        private IWaveProvider AudioSamplesProvider;
        private CircularBuffer AudioBuffer;
        private bool IsDisposed = false;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioRenderer"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public AudioRenderer(MediaElement mediaElement)
        {
            MediaElement = mediaElement;
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

            if (AudioSamplesProvider == null)
                AudioSamplesProvider = new CallbackWaveProvider(ProvideAudioSamplesCallback);

            AudioDevice = new WaveOutEvent()
            {
                DesiredLatency = 200,
                NumberOfBuffers = 2,
            };

            var bufferLength = AudioSamplesProvider.WaveFormat.ConvertLatencyToByteSize(AudioDevice.DesiredLatency) * MediaElement.Blocks[MediaType.Audio].Capacity / 2;
            AudioBuffer = new CircularBuffer(bufferLength);
            AudioDevice.Init(AudioSamplesProvider);
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
        /// Callback providing the audio samples.
        /// </summary>
        /// <param name="requestedBytes">The requested bytes.</param>
        /// <returns></returns>
        private byte[] ProvideAudioSamplesCallback(int requestedBytes)
        {
            if (MediaElement.IsPlaying == false || MediaElement.HasAudio == false)
                return null;

            var result = AudioBuffer.ReadableCount >= requestedBytes ? AudioBuffer.Read(requestedBytes) : null;
            return result;
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
