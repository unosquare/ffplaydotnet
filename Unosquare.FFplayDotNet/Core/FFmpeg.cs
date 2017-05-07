namespace FFmpeg.AutoGen
{
    using System;
    using System.Runtime.InteropServices;

    unsafe partial class ffmpeg
    {
        #region Interop

        /// <summary>
        /// Gets the current time in microseconds since Jan 1, 1970
        /// </summary>
        /// <returns></returns>
        [DllImport("avutil-55", EntryPoint = "av_gettime", CallingConvention = CallingConvention.Cdecl)]
        public static extern long av_gettime();

        /// <summary>
        /// Get the current time in microseconds since some unspecified starting point.
        /// On platforms that support it, the time comes from a monotonic clock
        /// This property makes this time source ideal for measuring relative time.
        /// The returned values may not be monotonic on platforms where a monotonic
        /// clock is not available.
        /// </summary>
        /// <returns></returns>
        [DllImport("avutil-55", EntryPoint = "av_gettime_relative", CallingConvention = CallingConvention.Cdecl)]
        public static extern long av_gettime_relative();

        /// <summary>Scale the image slice in srcSlice and put the resulting scaled slice in the image in dst. A slice is a sequence of consecutive rows in an image.</summary>
        /// <param name="c">the scaling context previously created with sws_getContext()</param>
        /// <param name="srcSlice">the array containing the pointers to the planes of the source slice</param>
        /// <param name="srcStride">the array containing the strides for each plane of the source image</param>
        /// <param name="srcSliceY">the position in the source image of the slice to process, that is the number (counted starting from zero) in the image of the first row of the slice</param>
        /// <param name="srcSliceH">the height of the source slice, that is the number of rows in the slice</param>
        /// <param name="dst">the array containing the pointers to the planes of the destination image</param>
        /// <param name="dstStride">the array containing the strides for each plane of the destination image</param>
        [DllImport("swscale-4", EntryPoint = "sws_scale", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int sws_scale(SwsContext* @c, byte** @srcSlice, int* @srcStride, int @srcSliceY, int @srcSliceH, byte** @dst, int* @dstStride);


        [DllImport("avformat-57", EntryPoint = "avio_tell", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern long avio_tell(AVIOContext* @s);

        #endregion

        #region Ported Methods

        /// <summary>
        /// Clips a signed integer value into the amin-amax range.
        /// </summary>
        /// <param name="a">a.</param>
        /// <param name="amin">The amin.</param>
        /// <param name="amax">The amax.</param>
        /// <returns></returns>
        public static int av_clip(int a, int amin, int amax)
        {
            if (a > amax) return amax;
            if (a < amin) return amin;
            return a;
        }

        /// <summary>
        /// Converts rational to double.
        /// </summary>
        /// <param name="r">The r.</param>
        /// <returns></returns>
        public static double av_q2d(AVRational r)
        {
            return (double)r.num / r.den;
        }

        private static int MKTAG(params byte[] buff)
        {
            //  ((a) | ((b) << 8) | ((c) << 16) | ((unsigned)(d) << 24))
            return BitConverter.ToInt32(buff, 0);
        }

        private static int MKTAG(byte a, char b, char c, char d)
        {
            return MKTAG(new byte[] { a, (byte)b, (byte)c, (byte)d });
        }

        private static int MKTAG(char a, char b, char c, char d)
        {
            return MKTAG(new byte[] { (byte)a, (byte)b, (byte)c, (byte)d });
        }

        #endregion

        #region Constant Definitions

        public const long AV_NOPTS_VALUE = long.MinValue;
        public static readonly AVRational AV_TIME_BASE_Q = new AVRational { num = 1, den = AV_TIME_BASE }; // (AVRational){1, AV_TIME_BASE
        public static readonly int AVERROR_EOF = -MKTAG('E', 'O', 'F', ' '); // http://www-numi.fnal.gov/offline_software/srt_public_context/WebDocs/Errors/unix_system_errors.html
        public const int AVERROR_EAGAIN = -11; // http://www-numi.fnal.gov/offline_software/srt_public_context/WebDocs/Errors/unix_system_errors.html
        public const int AVERROR_ENOMEM = -12;
        public const int AVERROR_EINVAL = -22;
        public const int AVERROR_ENOSYS = -38;
        public const int AVERROR_NOTSUPP = -40;
        public static readonly int AVERROR_OPTION_NOT_FOUND = -MKTAG(0xF8, 'O', 'P', 'T');

        #endregion
    }
}
