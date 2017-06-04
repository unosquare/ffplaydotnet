namespace Unosquare.FFplayDotNet
{
    using Core;
    using Decoding;
    using NAudio.Wave;
    using System;

    partial class MediaElement
    {
        // TODO: Isolate the audio renderer in a separate class and implement IDisposable.

        private IWavePlayer AudioDevice;
        private IWaveProvider AudioSamplesProvider;
        private CircularBuffer AudioBuffer;

        private void InitializeAudio()
        {
            DestroyAudio();

            if (AudioSamplesProvider == null)
                AudioSamplesProvider = new CallbackWaveProvider16(ProvideAudioSamplesCallback);

            AudioBuffer = new CircularBuffer(AudioParams.Output.BufferLength); // Buffer length is 1 second (that is plenty)
            AudioDevice = new DirectSoundOut();

            AudioDevice.Init(AudioSamplesProvider);
        }

        private void DestroyAudio()
        {
            if (AudioBuffer != null)
            {
                AudioBuffer.Dispose();
                AudioBuffer = null;
            }

            if (AudioDevice == null) return;
            AudioDevice.Stop();
            AudioDevice.Dispose();
            AudioDevice = null;
        }

        private void PlayAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Play();
        }

        private void PauseAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Pause();
        }

        private void StopAudio()
        {
            if (AudioDevice == null)
                InitializeAudio();

            AudioDevice.Stop();
        }

        private void RenderAudio(AudioBlock block)
        {
            AudioBuffer.Write(block.Buffer, block.BufferLength);
        }

        private byte[] ProvideAudioSamplesCallback(int requestedBytes)
        {

            if (IsPlaying == false || HasAudio == false)
                return null;

            return AudioBuffer.Available >= requestedBytes ? AudioBuffer.Read(requestedBytes) : null;
        }

    }

    internal class CallbackWaveProvider16 : IWaveProvider
    {
        public delegate byte[] ProvideSamplesBufferDelegate(int wantedBytes);

        private ProvideSamplesBufferDelegate ProvideSamplesCallback = null;
        private WaveFormat m_Format = null;
        private byte[] SilenceBuffer = null;

        public CallbackWaveProvider16(ProvideSamplesBufferDelegate provideSamplesCallback)
        {
            m_Format = new WaveFormat(AudioParams.Output.SampleRate, AudioParams.OutputBitsPerSample, AudioParams.Output.ChannelCount);
            SilenceBuffer = new byte[m_Format.BitsPerSample / 8 * m_Format.Channels * 2];
            ProvideSamplesCallback = provideSamplesCallback;
        }

        /// <summary>
        /// Fill the specified buffer with wave data.
        /// </summary>
        /// <param name="buffer">The buffer to fill of wave data.</param>
        /// <param name="offset">Offset into buffer</param>
        /// <param name="count">The number of bytes to read</param>
        /// <returns>
        /// the number of bytes written to the buffer.
        /// </returns>
        public int Read(byte[] buffer, int offset, int count)
        {
            byte[] renderBuffer = null;

            try { renderBuffer = ProvideSamplesCallback(count); }
            catch { }
            if (renderBuffer == null) renderBuffer = new byte[] { };

            if (renderBuffer.Length == 0)
            {
                Buffer.BlockCopy(SilenceBuffer, 0, buffer, offset, Math.Min(SilenceBuffer.Length, buffer.Length));
                return SilenceBuffer.Length;
            }
            else
            {
                Buffer.BlockCopy(renderBuffer, 0, buffer, offset, renderBuffer.Length);
                return renderBuffer.Length;
            }

        }

        public WaveFormat WaveFormat
        {
            get { return m_Format; }
        }

    }

}
