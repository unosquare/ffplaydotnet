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

        private double m_Volume = 1.0d;
        private double m_Balance = 0.0d;
        private double m_LeftVolume = 1.0d;
        private double m_RightVolume = 1.0d;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallbackWaveProvider"/> class.
        /// </summary>
        /// <param name="provideSamplesCallback">The provide samples callback.</param>
        public CallbackWaveProvider(ProvideSamplesBufferDelegate provideSamplesCallback)
        {
            m_Format = new WaveFormat(AudioParams.Output.SampleRate, AudioParams.OutputBitsPerSample, AudioParams.Output.ChannelCount);
            if (WaveFormat.BitsPerSample != 16 || WaveFormat.Channels != 2)
                throw new NotSupportedException($"Wave Format has to be 16-bit and 2-channel.");

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

            try
            {
                renderBuffer = ProvideSamplesCallback(count);
            }
            catch { }
            if (renderBuffer == null) renderBuffer = new byte[] { };

            var isLeftSample = true;
            for (var baseIndex = 0; baseIndex < renderBuffer.Length; baseIndex += WaveFormat.BitsPerSample / 8)
            {
                var sample = BitConverter.ToInt16(new byte[] { renderBuffer[baseIndex], renderBuffer[baseIndex + 1] }, 0);

                if (isLeftSample)
                    sample = (short)(sample * m_LeftVolume);
                else
                    sample = (short)(sample * m_RightVolume);

                var resampledSample = BitConverter.GetBytes(sample);
                renderBuffer[baseIndex] = resampledSample[0];
                renderBuffer[baseIndex + 1] = resampledSample[1];

                isLeftSample = !isLeftSample;
            }

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

    }

}
