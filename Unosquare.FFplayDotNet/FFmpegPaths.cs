using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet
{
    /// <summary>
    /// Provides access to the paths where FFmpeg binaries are extracted to
    /// </summary>
    static public class FFmpegPaths
    {
        /// <summary>
        /// Initializes the <see cref="FFmpegPaths"/> class.
        /// </summary>
        static FFmpegPaths()
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
