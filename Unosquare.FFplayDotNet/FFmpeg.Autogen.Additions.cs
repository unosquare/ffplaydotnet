namespace FFmpeg.AutoGen
{
    using System.Runtime.InteropServices;

    public unsafe static partial class ffmpeg
    {
        /// <summary>
        /// Gets the current time in microseconds since Jan 1, 1970
        /// </summary>
        /// <returns></returns>
        [DllImport(libavutil, EntryPoint = "av_gettime", CallingConvention = CallingConvention.Cdecl)]
        public static extern long av_gettime();

        /// <summary>
        /// Get the current time in microseconds since some unspecified starting point.
        /// On platforms that support it, the time comes from a monotonic clock
        /// This property makes this time source ideal for measuring relative time.
        /// The returned values may not be monotonic on platforms where a monotonic
        /// clock is not available.
        /// </summary>
        /// <returns></returns>
        [DllImport(libavutil, EntryPoint = "av_gettime_relative", CallingConvention = CallingConvention.Cdecl)]
        public static extern long av_gettime_relative();
    }
}
