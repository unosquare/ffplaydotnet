namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// Performs video picture decoding, scaling and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class VideoComponent : MediaComponent
    {
        #region Private State Variables

        /// <summary>
        /// Holds a reference to the video scaler
        /// </summary>
        private SwsContext* Scaler = null;

        /// <summary>
        /// Holds a reference to the last allocated buffer
        /// </summary>
        private IntPtr PictureBuffer;

        /// <summary>
        /// The picture buffer length of the last allocated buffer
        /// </summary>
        private int PictureBufferLength;

        /// <summary>
        /// The picture buffer stride. 
        /// Pixel Width * 24-bit color (3 byes) + alignment (typically 0 for modern hw).
        /// </summary>
        private int PictureBufferStride;



        #endregion

        #region Constants

        /// <summary>
        /// Gets the video scaler flags used to perfom colorspace conversion (if needed).
        /// </summary>
        public static int ScalerFlags { get; internal set; } = ffmpeg.SWS_X; //ffmpeg.SWS_BICUBIC;

        /// <summary>
        /// The output pixel format BGR24 of the scaler.
        /// </summary>
        public const AVPixelFormat OutputPixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        internal VideoComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            BaseFrameRate = Stream->r_frame_rate.ToDouble();
            CurrentFrameRate = BaseFrameRate;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the base frame rate as reported by the stream component.
        /// All discrete timestamps can be represented in this framerate.
        /// </summary>
        public double BaseFrameRate { get; }

        /// <summary>
        /// Gets the current frame rate as guessed by the last processed frame.
        /// Variable framerate might report different values at different times.
        /// </summary>
        public double CurrentFrameRate { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the pixel format replacing deprecated pixel formats.
        /// AV_PIX_FMT_YUVJ
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <returns></returns>
        private static AVPixelFormat GetPixelFormat(AVFrame* frame)
        {
            var currentFormat = (AVPixelFormat)frame->format;
            switch (currentFormat)
            {
                case AVPixelFormat.AV_PIX_FMT_YUVJ411P: return AVPixelFormat.AV_PIX_FMT_YUV411P;
                case AVPixelFormat.AV_PIX_FMT_YUVJ420P: return AVPixelFormat.AV_PIX_FMT_YUV420P;
                case AVPixelFormat.AV_PIX_FMT_YUVJ422P: return AVPixelFormat.AV_PIX_FMT_YUV422P;
                case AVPixelFormat.AV_PIX_FMT_YUVJ440P: return AVPixelFormat.AV_PIX_FMT_YUV440P;
                case AVPixelFormat.AV_PIX_FMT_YUVJ444P: return AVPixelFormat.AV_PIX_FMT_YUV444P;
                default: return currentFormat;
            }

        }

        /// <summary>
        /// Processes the frame data by performing a framebuffer allocation, scaling the image
        /// and raising an event containing the bitmap.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            var renderTime = frame->pts.ToTimeSpan(Stream->time_base);
            var duration = ffmpeg.av_frame_get_pkt_duration(frame).ToTimeSpan(Stream->time_base);

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastFrameRenderTime = renderTime;

            // Update the current framerate
            CurrentFrameRate = ffmpeg.av_guess_frame_rate(Container.InputContext, Stream, frame).ToDouble();

            // If we don't have a callback, we don't need any further processing
            if (Container.HandlesOnVideoDataAvailable == false)
                return;

            // Retrieve a suitable scaler or create it on the fly
            Scaler = ffmpeg.sws_getCachedContext(Scaler,
                    frame->width, frame->height, GetPixelFormat(frame), frame->width, frame->height,
                    OutputPixelFormat, ScalerFlags, null, null, null);

            // Perform scaling and save the data to our unmanaged buffer pointer for callbacks
            {
                PictureBufferStride = ffmpeg.av_image_get_linesize(OutputPixelFormat, frame->width, 0);
                var targetStride = new int[] { PictureBufferStride };
                var targetLength = ffmpeg.av_image_get_buffer_size(OutputPixelFormat, frame->width, frame->height, 1);
                var unmanagedBuffer = AllocateBuffer(targetLength);
                var targetScan = new byte_ptrArray8();
                targetScan[0] = (byte*)unmanagedBuffer;
                var outputHeight = ffmpeg.sws_scale(Scaler, frame->data, frame->linesize, 0, frame->height, targetScan, targetStride);
            }

            // Raise the data available event with all the decompressed frame data
            Container.RaiseOnVideoDataAvailabe(
                PictureBuffer, PictureBufferLength, PictureBufferStride,
                frame->width, frame->height,
                renderTime,
                duration);

        }

        /// <summary>
        /// Processes the subtitle frame.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        /// <exception cref="System.NotSupportedException"></exception>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            throw new NotSupportedException($"{nameof(VideoComponent)} does not support subtitle frame processing.");
        }

        /// <summary>
        /// Allocates a buffer if needed in unmanaged memory. If we already have a buffer of the specified
        /// length, then the existing buffer is not freed and recreated. Regardless, this method will always return
        /// a pointer to the start of the buffer.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private IntPtr AllocateBuffer(int length)
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

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);
            if (Scaler != null)
                ffmpeg.sws_freeContext(Scaler);

            if (PictureBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(PictureBuffer);
        }

        #endregion
    }
}
