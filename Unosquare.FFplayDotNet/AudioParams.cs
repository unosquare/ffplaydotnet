namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;

    /// <summary>
    /// Represents audio parameters for audio frame decoding and resampling
    /// </summary>
    public class AudioParams
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AudioParams"/> class.
        /// </summary>
        public AudioParams()
        {
            // placeholder
        }

        /// <summary>
        /// Gets the frequency.
        /// </summary>
        public int Frequency { get; internal set; }

        /// <summary>
        /// Gets the channel count. 1for Mono, 2 for stereo and more than 2 for other formats.
        /// </summary>
        public int ChannelCount { get; internal set; }

        public long ChannelLayout { get; internal set; }

        public AVSampleFormat SampleFormat { get; internal set; }

        public int FrameSize { get; internal set; }

        public int BytesPerSecond { get; internal set; }

        /// <summary>
        /// Copies the parameter value to another audio parameters object.
        /// </summary>
        /// <param name="other">The other.</param>
        internal void CopyTo(AudioParams other)
        {
            other.BytesPerSecond = BytesPerSecond;
            other.ChannelCount = ChannelCount;
            other.ChannelLayout = ChannelLayout;
            other.SampleFormat = SampleFormat;
            other.FrameSize = FrameSize;
            other.Frequency = Frequency;
        }
    }

}
