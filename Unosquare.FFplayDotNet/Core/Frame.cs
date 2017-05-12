namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    /// <summary>
    /// A base class for frame containers of various types
    /// Frame containers are uncompressed frame data that can be used for
    /// media rendering
    /// </summary>
    public abstract class Frame : IComparable<Frame>
    {
        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public abstract MediaType MediaType { get; }

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public TimeSpan StartTime { get; internal set; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; internal set; }

        /// <summary>
        /// Gets the end time.
        /// </summary>
        public TimeSpan EndTime { get; internal set; }

        /// <summary>
        /// Compares the current instance with another object of the same type and returns an integer that indicates whether the current instance precedes, follows, or occurs in the same position in the sort order as the other object.
        /// </summary>
        /// <param name="other">An object to compare with this instance.</param>
        /// <returns>
        /// A value that indicates the relative order of the objects being compared. The return value has these meanings: Value Meaning Less than zero This instance precedes <paramref name="other" /> in the sort order.  Zero This instance occurs in the same position in the sort order as <paramref name="other" />. Greater than zero This instance follows <paramref name="other" /> in the sort order.
        /// </returns>
        public int CompareTo(Frame other)
        {
            return StartTime.CompareTo(other.StartTime);
        }
    }

    /// <summary>
    /// A video frame container. The buffer is in BGR, 24-bit format
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.Frame" />
    /// <seealso cref="System.IDisposable" />
    public sealed class VideoFrame : Frame, IDisposable
    {
        #region Private Members

        private bool IsDisposed = false; // To detect redundant calls

        #endregion

        #region Properties

        /// <summary>
        /// The picture buffer length of the last allocated buffer
        /// </summary>
        internal int PictureBufferLength;

        /// <summary>
        /// Holds a reference to the last allocated buffer
        /// </summary>
        internal IntPtr PictureBuffer;

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public override MediaType MediaType => MediaType.Video;

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format is 24bit BGR
        /// </summary>
        public IntPtr Buffer { get { return PictureBuffer; } }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get { return PictureBufferLength; } }

        /// <summary>
        /// The picture buffer stride. 
        /// Pixel Width * 24-bit color (3 byes) + alignment (typically 0 for modern hw).
        /// </summary>
        public int BufferStride { get; internal set; }

        /// <summary>
        /// Gets the number of horizontal pixels in the image.
        /// </summary>
        public int PixelWidth { get; internal set; }

        /// <summary>
        /// Gets the number of vertical pixels in the image.
        /// </summary>
        public int PixelHeight { get; internal set; }

        /// <summary>
        /// Gets the coded picture number.
        /// </summary>
        public int CodedPictureNumber { get; internal set; }

        /// <summary>
        /// Gets the display picture number.
        /// </summary>
        public int DisplayPictureNumber { get; internal set; }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        void Dispose(bool alsoManaged)
        {
            if (IsDisposed) return;
            if (alsoManaged)
            {
                if (PictureBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(PictureBuffer);
                    PictureBuffer = IntPtr.Zero;
                }

            }

            IsDisposed = true;
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

    /// <summary>
    /// An audio frame container. The buffer is in 16-bit signed, interleaved sample data
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.Frame" />
    /// <seealso cref="System.IDisposable" />
    public sealed class AudioFrame : Frame, IDisposable
    {
        #region Private Members

        private bool IsDisposed = false; // To detect redundant calls

        #endregion

        #region Properties

        /// <summary>
        /// The picture buffer length of the last allocated buffer
        /// </summary>
        internal int AudioBufferLength;

        /// <summary>
        /// Holds a reference to the last allocated buffer
        /// </summary>
        internal IntPtr AudioBuffer;

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        /// <exception cref="System.NotImplementedException"></exception>
        public override MediaType MediaType => MediaType.Audio;

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format signed 16-bits per sample, channel interleaved
        /// </summary>
        public IntPtr Buffer { get { return AudioBuffer; } }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get; internal set; }

        /// <summary>
        /// Gets the sample rate.
        /// </summary>
        public int SampleRate { get; internal set; }

        /// <summary>
        /// Gets the channel count.
        /// </summary>
        public int ChannelCount { get; internal set; }

        /// <summary>
        /// Gets the available samples per channel.
        /// </summary>
        public int SamplesPerChannel { get; internal set; }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        void Dispose(bool alsoManaged)
        {
            if (IsDisposed) return;

            if (alsoManaged)
            {
                if (AudioBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(AudioBuffer);
                    AudioBuffer = IntPtr.Zero;
                }

            }

            IsDisposed = true;
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

    /// <summary>
    /// A subtitle frame container. Simply contains text lines.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.Frame" />
    public sealed class SubtitleFrame : Frame
    {
        #region Properties

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public override MediaType MediaType => MediaType.Subtitle;

        /// <summary>
        /// Gets the lines of text for this subtitle frame.
        /// </summary>
        public List<string> Text { get; } = new List<string>(16);

        #endregion
    }
}
