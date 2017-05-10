namespace Unosquare.FFplayDotNet.Core
{
    using System;

    /// <summary>
    /// Represents a base class for uncompressed media events
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    public abstract class MediaDataAvailableEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        protected MediaDataAvailableEventArgs(MediaType mediaType, TimeSpan renderTime, TimeSpan duration)
        {
            MediaType = mediaType;
            RenderTime = renderTime;
            Duration = duration;
        }

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public TimeSpan RenderTime { get; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public MediaType MediaType { get; }
    }

    /// <summary>
    /// Contains result data for subtitle frame processing.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaDataAvailableEventArgs" />
    public class SubtitleDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="textLines">The text lines.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="endTime">The end time.</param>
        /// <param name="duration">The duration.</param>
        internal SubtitleDataAvailableEventArgs(string[] textLines, TimeSpan renderTime, TimeSpan endTime, TimeSpan duration)
            : base(MediaType.Subtitle, renderTime, duration)
        {
            TextLines = textLines ?? new string[] { };
            EndTime = endTime;
        }

        /// <summary>
        /// Gets the lines of text to be displayed.
        /// </summary>
        public string[] TextLines { get; }

        /// <summary>
        /// Gets the time at which this subtitle must stop displaying
        /// </summary>
        public TimeSpan EndTime { get; }

    }

    /// <summary>
    /// Contains result data for audio frame processing.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaDataAvailableEventArgs" />
    public class AudioDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="sampleRate">The sample rate.</param>
        /// <param name="samplesPerChannel">The samples per channel.</param>
        /// <param name="channels">The channels.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal AudioDataAvailableEventArgs(IntPtr buffer, int bufferLength, int sampleRate, int samplesPerChannel, int channels,
            TimeSpan renderTime, TimeSpan duration)
            : base(MediaType.Audio, renderTime, duration)
        {
            Buffer = buffer;
            BufferLength = bufferLength;
            SamplesPerChannel = samplesPerChannel;
            SampleRate = sampleRate;
            Channels = channels;
        }

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format is interleaved stereo 16-bit, signed samples (4 bytes per sample in stereo)
        /// </summary>
        public IntPtr Buffer { get; }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get; }

        /// <summary>
        /// Gets the sample rate of the buffer.
        /// Typically this is 4.8kHz
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Gets the number of samples in the data per individual audio channel.
        /// </summary>
        public int SamplesPerChannel { get; }

        /// <summary>
        /// Gets the number of channels the data buffer represents.
        /// </summary>
        public int Channels { get; }
    }

    /// <summary>
    /// Contains result data for video frame processing.
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    public class VideoDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VideoDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="bufferStride">The buffer stride.</param>
        /// <param name="width">The width.</param>
        /// <param name="height">The height.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal VideoDataAvailableEventArgs(IntPtr buffer, int bufferLength, int bufferStride, int width, int height,
            TimeSpan renderTime, TimeSpan duration)
            : base(MediaType.Video, renderTime, duration)
        {
            Buffer = buffer;
            BufferLength = bufferLength;
            BufferStride = bufferStride;
            PixelWidth = width;
            PixelHeight = height;
        }

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format is 24bit BGR
        /// </summary>
        public IntPtr Buffer { get; }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get; }

        /// <summary>
        /// Gets the number of bytes per scanline in the image
        /// </summary>
        public int BufferStride { get; }

        /// <summary>
        /// Gets the number of horizontal pixels in the image.
        /// </summary>
        public int PixelWidth { get; }

        /// <summary>
        /// Gets the number of vertical pixels in the image.
        /// </summary>
        public int PixelHeight { get; }
    }

    public class MediaEndOfStreamEventArgs : EventArgs
    {
        public MediaEndOfStreamEventArgs(object sender) : base() { }
    }

}
