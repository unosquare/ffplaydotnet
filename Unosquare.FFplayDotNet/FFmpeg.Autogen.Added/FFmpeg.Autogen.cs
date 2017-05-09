namespace FFmpeg.AutoGen
{
    using System;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;

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

        [DllImport("avformat-57", EntryPoint = "avio_tell", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern long avio_tell(AVIOContext* @s);

        #endregion

        #region Ported Methods

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

        public const long AV_NOPTS = long.MinValue;
        public static readonly AVRational AV_TIME_BASE_Q = new AVRational { num = 1, den = AV_TIME_BASE };
        public static readonly int AVERROR_EOF = -MKTAG('E', 'O', 'F', ' '); // http://www-numi.fnal.gov/offline_software/srt_public_context/WebDocs/Errors/unix_system_errors.html
        public const int AVERROR_EAGAIN = -11; // http://www-numi.fnal.gov/offline_software/srt_public_context/WebDocs/Errors/unix_system_errors.html
        public const int AVERROR_ENOMEM = -12;
        public const int AVERROR_EINVAL = -22;
        public const int AVERROR_ENOSYS = -38;
        public const int AVERROR_NOTSUPP = -40;
        public static readonly int AVERROR_OPTION_NOT_FOUND = -MKTAG(0xF8, 'O', 'P', 'T');

        #endregion

        #region Utility

        /// <summary>
        /// Gets the FFmpeg error mesage based on the error code
        /// </summary>
        /// <param name="code">The code.</param>
        /// <returns></returns>
        public static unsafe string ErrorMessage(int code)
        {
            var errorStrBytes = new byte[1024];
            var errorStrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(byte)) * errorStrBytes.Length);
            ffmpeg.av_strerror(code, (byte*)errorStrPtr, (ulong)errorStrBytes.Length);
            Marshal.Copy(errorStrPtr, errorStrBytes, 0, errorStrBytes.Length);
            Marshal.FreeHGlobal(errorStrPtr);

            var errorMessage = Encoding.GetEncoding(0).GetString(errorStrBytes).Split('\0').FirstOrDefault();
            return errorMessage;
        }

        #endregion
    }
}
