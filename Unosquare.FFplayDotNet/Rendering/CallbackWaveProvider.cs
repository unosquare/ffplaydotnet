namespace Unosquare.FFplayDotNet.Rendering
{
    using Core;
    using NAudio.Wave;
    using System;

    /// <summary>
    /// Represent a 16-bit callback wave provider. Provide a wave sample reading delegate
    /// and it will be called continuously.
    /// </summary>
    /// <seealso cref="NAudio.Wave.IWaveProvider" />
    internal sealed class CallbackWaveProvider : IWaveProvider
    {
        public delegate byte[] ProvideSamplesBufferDelegate(int wantedBytes);

        private ProvideSamplesBufferDelegate ProvideSamplesCallback = null;
        private WaveFormat m_Format = null;
        private byte[] SilenceBuffer = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallbackWaveProvider"/> class.
        /// </summary>
        /// <param name="provideSamplesCallback">The provide samples callback.</param>
        public CallbackWaveProvider(ProvideSamplesBufferDelegate provideSamplesCallback)
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

    }

}
