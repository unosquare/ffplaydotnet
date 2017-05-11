using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Core
{

    public abstract class MediaFrameSlot
    {

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public abstract MediaType MediaType { get; }

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public TimeSpan StartTime { get; protected set; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; protected set; }

        /// <summary>
        /// Gets or sets the end time.
        /// </summary>
        public TimeSpan EndTime { get; protected set; }

    }

    public class VideoFrameSlot : MediaFrameSlot, IDisposable
    {
        /// <summary>
        /// The picture buffer length of the last allocated buffer
        /// </summary>
        private int PictureBufferLength;

        /// <summary>
        /// The picture buffer stride. 
        /// Pixel Width * 24-bit color (3 byes) + alignment (typically 0 for modern hw).
        /// </summary>
        private int PictureBufferStride;

        /// <summary>
        /// Holds a reference to the last allocated buffer
        /// </summary>
        private IntPtr PictureBuffer;

        /// <summary>
        /// Allocates a buffer if needed in unmanaged memory. If we already have a buffer of the specified
        /// length, then the existing buffer is not freed and recreated. Regardless, this method will always return
        /// a pointer to the start of the buffer.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        public IntPtr Allocate(int length)
        {
            // If there is a size mismatch between the wanted buffer length and the existing one,
            // then let's reallocate the buffer and set the new size (dispose of the existing one if any)
            if (PictureBufferLength != length)
            {
                if (PictureBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(PictureBuffer);

                PictureBufferLength = length;
                PictureBuffer = Marshal.AllocHGlobal(PictureBufferLength);
            }

            return PictureBuffer;
        }

        public void Update(TimeSpan startTime, TimeSpan endTime, TimeSpan duration, 
            int bufferStride, int pixelWidth, int pixelHeight, int codedPictureNumber, int displayPictureNumber)
        {
            StartTime = startTime;
            EndTime = endTime;
            Duration = duration;
            BufferStride = bufferStride;
            PixelWidth = pixelWidth;
            PixelHeight = PixelHeight;
            CodedPictureNumber = codedPictureNumber;
            DisplayPictureNumber = displayPictureNumber;
        }

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
        /// Gets the number of bytes per scanline in the image
        /// </summary>
        public int BufferStride { get; protected set; }

        /// <summary>
        /// Gets the number of horizontal pixels in the image.
        /// </summary>
        public int PixelWidth { get; protected set; }

        /// <summary>
        /// Gets the number of vertical pixels in the image.
        /// </summary>
        public int PixelHeight { get; protected set; }

        /// <summary>
        /// Gets the coded picture number.
        /// </summary>
        public int CodedPictureNumber { get; protected set; }

        /// <summary>
        /// Gets the display picture number.
        /// </summary>
        public int DisplayPictureNumber { get; protected set; }

        #region IDisposable Support
        private bool IsDisposed = false; // To detect redundant calls

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

    public class AudioFrameSlot : MediaFrameSlot
    {
        public override MediaType MediaType => throw new NotImplementedException();
    }

    public class SubtitleFrameSlot : MediaFrameSlot
    {
        public override MediaType MediaType => throw new NotImplementedException();
    }
}
