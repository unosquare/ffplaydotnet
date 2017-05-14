namespace Unosquare.FFplayDotNet.Decoding
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
        public static int ScalerFlags { get; internal set; } = ffmpeg.SWS_BICUBIC;

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
            if (double.IsNaN(CurrentFrameRate))
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
        /// Creates a frame source object given the raw FFmpeg frame reference.
        /// </summary>
        /// <param name="frame">The raw FFmpeg frame pointer.</param>
        /// <param name="packet">The packet.</param>
        /// <returns></returns>
        protected override unsafe FrameSource CreateFrameSource(AVFrame* frame, AVPacket* packet)
        {
            var frameHolder = new VideoFrameSource(frame, packet, Stream);
            CurrentFrameRate = ffmpeg.av_guess_frame_rate(Container.InputContext, Stream, frame).ToDouble();
            return frameHolder;
        }

        /// <summary>
        /// Converts decoded, raw frame data in the frame source into a a usable frame. <br />
        /// The process includes performing picture, samples or text conversions
        /// so that the decoded source frame data is easily usable in multimedia applications
        /// </summary>
        /// <param name="input">The source frame to use as an input.</param>
        /// <param name="output">The target frame that will be updated with the source frame. If null is passed the frame will be instantiated.</param>
        /// <returns>
        /// Return the updated output frame
        /// </returns>
        /// <exception cref="System.ArgumentNullException">input</exception>
        internal override Frame MaterializeFrame(FrameSource input, ref Frame output)
        {
            if (output == null) output = new VideoFrame();
            var source = input as VideoFrameSource;
            var target = output as VideoFrame;

            if (source == null || target == null)
                throw new ArgumentNullException($"{nameof(input)} and {nameof(output)} are either null or not of a compatible media type '{MediaType}'");

            // Retrieve a suitable scaler or create it on the fly
            Scaler = ffmpeg.sws_getCachedContext(Scaler,
                    source.Pointer->width, source.Pointer->height, GetPixelFormat(source.Pointer),
                    source.Pointer->width, source.Pointer->height,
                    OutputPixelFormat, ScalerFlags, null, null, null);

            // Perform scaling and save the data to our unmanaged buffer pointer
            var targetBufferStride = ffmpeg.av_image_get_linesize(OutputPixelFormat, source.Pointer->width, 0);
            var targetStride = new int[] { targetBufferStride };
            var targetLength = ffmpeg.av_image_get_buffer_size(OutputPixelFormat, source.Pointer->width, source.Pointer->height, 1);

            // Ensure proper allocation of the buffer
            // If there is a size mismatch between the wanted buffer length and the existing one,
            // then let's reallocate the buffer and set the new size (dispose of the existing one if any)
            if (target.PictureBufferLength != targetLength)
            {
                if (target.PictureBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(target.PictureBuffer);

                target.PictureBufferLength = targetLength;
                target.PictureBuffer = Marshal.AllocHGlobal(target.PictureBufferLength);
            }

            var targetScan = new byte_ptrArray8();
            targetScan[0] = (byte*)target.PictureBuffer;

            // The scaling is done here
            var outputHeight = ffmpeg.sws_scale(Scaler, source.Pointer->data, source.Pointer->linesize, 0, source.Pointer->height, targetScan, targetStride);

            // We set the target properties
            target.EndTime = source.EndTime;
            target.StartTime = source.StartTime;
            target.BufferStride = targetStride[0];
            target.CodedPictureNumber = source.Pointer->coded_picture_number;
            target.DisplayPictureNumber = source.Pointer->display_picture_number;
            target.Duration = source.Duration;
            target.PixelHeight = source.Pointer->height;
            target.PixelWidth = source.Pointer->width;


            return target;
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
