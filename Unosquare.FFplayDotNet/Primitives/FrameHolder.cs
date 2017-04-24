﻿namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using System.Runtime.InteropServices;
    using Unosquare.FFplayDotNet.Core;
    using static Unosquare.FFplayDotNet.SDL;

    /// <summary>
    /// A class that holds decoded frames of any media type.
    /// Port of struct Frame
    /// </summary>
    public unsafe class FrameHolder
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrameHolder"/> class.
        /// </summary>
        public FrameHolder()
        {
            // placeholder
        }

        /// <summary>
        /// The decoded frame. Port of *frame
        /// </summary>
        internal AVFrame* DecodedFrame;

        /// <summary>
        /// The decoded subtitle (if it is a subtitle frame)
        /// </summary>
        internal AVSubtitle Subtitle;

        /// <summary>
        /// Gets the type of the media of this frame.
        /// </summary>
        public AVMediaType MediaType { get; internal set; }

        /// <summary>
        /// The serial number of the last packet that
        /// composed the frame
        /// </summary>
        public int Serial { get; internal set; }

        /// <summary>
        /// The PTS: presentation timestamp for the frame
        /// </summary>
        public double Pts { get; internal set; }

        /// <summary>
        /// The estimated duration of the frame
        /// </summary>
        public double Duration { get; internal set; }

        /// <summary>
        /// The byte position of the frame in the input file
        /// </summary>
        public long BytePosition { get; internal set; }

        public BitmapBuffer Bitmap;

        public bool IsAllocated;
        public AVRational PictureAspectRatio;
        public int PictureWidth;
        public int PictureHeight;
        public int format;
        public bool IsUploaded;

        /// <summary>
        /// Fills the bitmap data from the decoded frame.
        /// </summary>
        internal void FillBitmapDataFromDecodedFrame()
        {
            FillBitmapDataFromBuffer(DecodedFrame->data[0], DecodedFrame->linesize[0] * DecodedFrame->height);
        }

        /// <summary>
        /// Fills the bitmap data from buffer.
        /// Port of SDL_UpdateTexture
        /// </summary>
        /// <param name="baseAddress">The base address.</param>
        /// <param name="byteLength">Length of the byte.</param>
        internal void FillBitmapDataFromBuffer(byte* baseAddress, int byteLength)
        {
            if (DecodedFrame == null)
                return;

            //var byteLength = DecodedFrame->linesize[0] * DecodedFrame->height;
            var targetPixels = new byte[byteLength];
            var pinnedArray = GCHandle.Alloc(targetPixels, GCHandleType.Pinned);
            //Native.memcpy((byte*)pinnedArray.AddrOfPinnedObject(), DecodedFrame->data[0], byteLength);
            Native.memcpy((byte*)pinnedArray.AddrOfPinnedObject(), baseAddress, byteLength);
            pinnedArray.Free();

            Bitmap = new BitmapBuffer();
            Bitmap.Data = targetPixels;
        }

        /// <summary>
        /// Fills the bitmap data from scaler.
        /// Port of SDL_LockTexture and SDL_UnlockTexture
        /// </summary>
        /// <param name="scaler">The scaler.</param>
        internal void FillBitmapDataFromScaler(SwsContext* scaler)
        {
            var sourceScan0 = DecodedFrame->data[0];
            var sourceStride = DecodedFrame->linesize[0];
            var targetStride = ffmpeg.av_image_get_linesize(AVPixelFormat.AV_PIX_FMT_BGRA, PictureWidth, PictureHeight);
            var targetLength = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, PictureWidth, PictureHeight, 1);

            var targetPixels = new byte[targetLength];
            var targetPixelsHandle = GCHandle.Alloc(targetPixels);
            var targetScan0 = (byte*)targetPixelsHandle.AddrOfPinnedObject();

            ffmpeg.sws_scale(scaler, &sourceScan0, &sourceStride, 0, DecodedFrame->height, &targetScan0, &targetStride);
            targetPixelsHandle.Free();

            if (Bitmap == null) Bitmap = new BitmapBuffer();
            Bitmap.Data = targetPixels;
        }
    }
}
