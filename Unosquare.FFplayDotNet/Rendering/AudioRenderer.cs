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
        private WaveOut AudioDevice;
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

            AudioDevice = new WaveOut(WaveCallbackInfo.FunctionCallback())
            {
                DesiredLatency = 80,
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

            MediaElement.Container.Log(MediaLogMessageType.Trace, $"{MediaType.Audio} WROTE: {addedBlockCount} blocks, {addedBytes} b | AVL: {AudioBuffer.ReadableCount} | LEN: {AudioBuffer.Length} | USE: {100.0 * AudioBuffer.ReadableCount / AudioBuffer.Length:0.00}%");
        }

        public void Play()
        {
            if (AudioDevice == null)
                Initialize();

            AudioDevice.Play();
        }

        public void Pause()
        {
            if (AudioDevice == null)
                Initialize();

            AudioDevice.Pause();
        }

        public void Stop()
        {
            if (AudioDevice == null)
                Initialize();

            AudioDevice.Stop();
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
            MediaElement.Container.Log(MediaLogMessageType.Trace, $"{MediaType.Audio} READ: {result.Length} b | AVL: {AudioBuffer.ReadableCount} | LEN: {AudioBuffer.Length} | USE: {100.0 * AudioBuffer.ReadableCount / AudioBuffer.Length:0.00}%");
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
