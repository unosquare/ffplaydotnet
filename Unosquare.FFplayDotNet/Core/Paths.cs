namespace Unosquare.FFplayDotNet.Core
{
    /// <summary>
    /// Provides access to the paths where FFmpeg binaries are extracted to
    /// </summary>
    static public class Paths
    {
        /// <summary>
        /// Initializes the <see cref="Paths"/> class.
        /// </summary>
        static Paths()
        {
            Helper.RegisterFFmpeg();
        }

        /// <summary>
        /// Gets the path to where the FFmpeg binaries are stored
        /// </summary>
        public static string BasePath { get; internal set; }

        /// <summary>
        /// Gets the full path to ffmpeg.exe
        /// </summary>
        public static string FFmpeg { get; internal set; }
        /// <summary>
        /// Gets the full path to ffprobe.exe
        /// </summary>
        public static string FFprobe { get; internal set; }

        /// <summary>
        /// Gets the full path to ffplay.exe
        /// </summary>
        public static string FFplay { get; internal set; }
    }
}
