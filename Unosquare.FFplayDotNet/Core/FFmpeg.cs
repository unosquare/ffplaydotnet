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

        /// <summary>
        /// Clips a signed integer value into the amin-amax range.
        /// </summary>
        /// <param name="a">a.</param>
        /// <param name="amin">The amin.</param>
        /// <param name="amax">The amax.</param>
        /// <returns></returns>
        [DllImport("avutil-55", EntryPoint = "av_clip", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int av_clip(int a, int amin, int amax);

        /// <summary>
        /// Converts rational to double.
        /// </summary>
        /// <param name="r">The r.</param>
        /// <returns></returns>
        [DllImport("avutil-55", EntryPoint = "av_q2d", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern double av_q2d(AVRational r);

        #endregion

        #region Ported Methods

        public static int MKTAG(params byte[] buff)
        {
            //  ((a) | ((b) << 8) | ((c) << 16) | ((unsigned)(d) << 24))
            return BitConverter.ToInt32(buff, 0);
        }

        public static int MKTAG(byte a, char b, char c, char d)
        {
            return MKTAG(new byte[] { a, (byte)b, (byte)c, (byte)d });
        }

        public static void memset<T>(Array arr, T value, int repeat)
        {
            var typedArray = arr as T[];
            for (var i = 0; i < repeat; i++)
                typedArray[i] = value;
        }

        public static void memset(byte* arr, byte value, int repeat)
        {
            byte* pArr = arr;

            // Copy the specified number of bytes from source to target.
            for (int i = 0; i < repeat; i++)
            {
                *pArr = value;
                pArr++;
            }
        }

        public static void memcpy(byte* target, byte* source, int length)
        {
            // Set the starting points in source and target for the copying.
            byte* ps = source;
            byte* pt = target;

            // Copy the specified number of bytes from source to target.
            for (int i = 0; i < length; i++)
            {
                *pt = *ps;
                pt++;
                ps++;
            }
        }

        public static TimeSpan ToTimeSpan(long ffmpegTimestamp)
        {
            var totalSeconds = (double)ffmpegTimestamp / ffmpeg.AV_TIME_BASE;
            return TimeSpan.FromSeconds(totalSeconds);
        }

        #endregion

        #region Constant Definitions

        public const long AV_NOPTS_VALUE = long.MinValue;
        public static readonly AVRational AV_TIME_BASE_Q = new AVRational { num = 1, den = AV_TIME_BASE }; // (AVRational){1, AV_TIME_BASE}

        public const int AVERROR_EOF = -32; // http://www-numi.fnal.gov/offline_software/srt_public_context/WebDocs/Errors/unix_system_errors.html
        public const int AVERROR_EAGAIN = -11;
        public const int AVERROR_ENOMEM = -12;
        public const int AVERROR_EINVAL = -22;
        public const int AVERROR_NOTSUPP = -40;
        public static readonly int AVERROR_OPTION_NOT_FOUND = -MKTAG(0xF8, 'O', 'P', 'T');

        #endregion
    }
}
