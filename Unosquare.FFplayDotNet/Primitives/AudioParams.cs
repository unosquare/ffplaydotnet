namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;

    /// <summary>
    /// Represents audio parameters for audio frame decoding and resampling
    /// Port of struct AudioParams
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
        /// Port of channels
        /// </summary>
        public int ChannelCount { get; internal set; }

        /// <summary>
        /// Gets the channel layout.
        /// Port of channel_layout
        /// </summary>
        public long ChannelLayout { get; internal set; }

        /// <summary>
        /// Gets the sample format.
        /// Port of fmt
        /// </summary>
        public AVSampleFormat SampleFormat { get; internal set; }

        /// <summary>
        /// Gets the length of the buffer for a samples of a single audio channel.
        /// Port of frame_size
        /// </summary>
        public int SampleBufferLength { get; internal set; }

        /// <summary>
        /// Gets the bytes to feed tha audio mixer per second.
        /// Port of bytes_per_sec
        /// </summary>
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
            other.SampleBufferLength = SampleBufferLength;
            other.Frequency = Frequency;
        }
    }

}
