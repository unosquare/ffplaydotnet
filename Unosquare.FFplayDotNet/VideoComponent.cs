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


        #endregion

        #region Constants

        /// <summary>
        /// Gets the video scaler flags used to perfom colorspace conversion (if needed).
        /// </summary>
        public static int ScalerFlags { get; internal set; } = ffmpeg.SWS_X; //ffmpeg.SWS_BICUBIC;

        /// <summary>
        /// The output pixel format of the scaler: 24-bit BGR
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
            CurrentFrameRate = Stream->avg_frame_rate.ToDouble();
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

        protected override unsafe Frame CreateFrame(AVFrame* frame)
        {
            var frameHolder = new VideoFrame(frame, Stream->time_base);
            CurrentFrameRate = ffmpeg.av_guess_frame_rate(Container.InputContext, Stream, frame).ToDouble();
            return frameHolder;
        }

        protected override void DequeueFrame(Frame genericFrame, MediaFrameSlot output)
        {
            var frame = genericFrame as VideoFrame;
            var slot = output as VideoFrameSlot;

            // If we don't have a callback, we don't need any further processing
            if (Container.HandlesOnVideoDataAvailable == false)
                return;

            // Retrieve a suitable scaler or create it on the fly
            Scaler = ffmpeg.sws_getCachedContext(Scaler,
                    frame.Pointer->width, frame.Pointer->height, GetPixelFormat(frame.Pointer), 
                    frame.Pointer->width, frame.Pointer->height,
                    OutputPixelFormat, ScalerFlags, null, null, null);

            // Perform scaling and save the data to our unmanaged buffer pointer for callbacks
            
                var targetBufferStride = ffmpeg.av_image_get_linesize(OutputPixelFormat, frame.Pointer->width, 0);
                var targetStride = new int[] { targetBufferStride };
                var targetLength = ffmpeg.av_image_get_buffer_size(OutputPixelFormat, frame.Pointer->width, frame.Pointer->height, 1);
                var targetBuffer = slot.Allocate(targetLength);
                var targetScan = new byte_ptrArray8();
                targetScan[0] = (byte*)targetBuffer;

                var outputHeight = ffmpeg.sws_scale(Scaler, frame.Pointer->data, frame.Pointer->linesize, 0, frame.Pointer->height, targetScan, targetStride);

                slot.Update(frame.StartTime, frame.EndTime, frame.Duration, targetStride[0], frame.Pointer->width, frame.Pointer->height, 
                    frame.Pointer->coded_picture_number, frame.Pointer->display_picture_number);
            


            // Raise the data available event with all the decompressed frame data
            Container.RaiseOnVideoDataAvailabe(slot.Buffer, slot.BufferLength, slot.BufferStride, slot.PixelWidth, slot.PixelHeight, slot.StartTime, slot.Duration);

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
        }

        #endregion
    }
}
