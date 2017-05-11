using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Core
{

    /// <summary>
    /// A base class for frame containers of various types
    /// Frame containers are uncompressed frame data that can be used for
    /// media rendering
    /// </summary>
    public abstract class FrameContainer
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
    }

    public class VideoFrameContainer : FrameContainer, IDisposable
    {
        private bool IsDisposed = false; // To detect redundant calls

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

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    if (PictureBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(PictureBuffer);
                        PictureBuffer = IntPtr.Zero;
                    }
                        
                }

                IsDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }

    public class AudioFrameContainer : FrameContainer, IDisposable
    {
        private bool IsDisposed = false; // To detect redundant calls
        
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

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    if (AudioBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(AudioBuffer);
                        AudioBuffer = IntPtr.Zero;
                    }

                }

                IsDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion


    }

    public class SubtitleFrameContainer : FrameContainer
    {
        public override MediaType MediaType => MediaType.Subtitle;

        public List<string> Text { get; } = new List<string>(16);
    }
}
