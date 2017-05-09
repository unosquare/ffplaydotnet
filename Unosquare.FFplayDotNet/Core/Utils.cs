namespace Unosquare.FFplayDotNet.Core
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Windows;

    /// <summary>
    /// Provides a set of utilities to perfrom conversion and other
    /// miscellaneous calculations
    /// </summary>
    public static class Utils
    {
        #region Private Declarations

        static private bool HasFFmpegRegistered = false;
        static private bool? isInDesignTime;
        static private readonly object FFmpegRegisterLock = new object();

        #endregion

        #region Interop

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32")]
        public static extern void CopyMemory(IntPtr destination, IntPtr source, uint length);

        #endregion

        #region Native

        /// <summary>
        /// Converts a byte pointer to a string
        /// </summary>
        /// <param name="bytePtr">The byte PTR.</param>
        /// <returns></returns>
        public static unsafe string PtrToString(byte* bytePtr)
        {
            return Marshal.PtrToStringAnsi(new IntPtr(bytePtr));
        }

        /// <summary>
        /// Converts a byte pointer to a UTF8 encoded string.
        /// </summary>
        /// <param name="bytePtr">The byte PTR.</param>
        /// <returns></returns>
        public static unsafe string PtrToStringUTF8(byte* bytePtr)
        {
            if (bytePtr == null) return null;
            if (*bytePtr == 0) return string.Empty;

            var byteBuffer = new List<byte>(1024);
            var currentByte = default(byte);

            while (true)
            {
                currentByte = *bytePtr;
                if (currentByte == 0)
                    break;

                byteBuffer.Add(currentByte);
                bytePtr++;
            }

            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }


        #endregion

        #region Math 

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts, AVRational timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS)
                return TimeSpan.MinValue;

            if (timeBase.den == 0)
                return TimeSpan.FromTicks((long)(TimeSpan.TicksPerMillisecond * 1000 * pts / ffmpeg.AV_TIME_BASE)); //) .FromSeconds(pts / ffmpeg.AV_TIME_BASE);

            return TimeSpan.FromTicks((long)(TimeSpan.TicksPerMillisecond * 1000 * pts * timeBase.num / timeBase.den)); //pts * timeBase.num / timeBase.den);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts, AVRational timeBase)
        {
            return ((double)pts).ToTimeSpan(timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts, double timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS)
                return TimeSpan.MinValue;

            return TimeSpan.FromTicks((long)(TimeSpan.TicksPerMillisecond * 1000 * pts / timeBase)); //pts / timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts, double timeBase)
        {
            return ((double)pts).ToTimeSpan(timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp (in AV_TIME_BASE units)
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts)
        {
            return ToTimeSpan(pts, ffmpeg.AV_TIME_BASE);
        }

        /// <summary>
        /// Gets a timespan given a timestamp (in AV_TIME_BASE units)
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts)
        {
            return ((double)pts).ToTimeSpan();
        }

        /// <summary>
        /// Converts a fraction to a double
        /// </summary>
        /// <param name="rational">The rational.</param>
        /// <returns></returns>
        public static double ToDouble(this AVRational rational)
        {
            return (double)rational.num / rational.den;
        }

        /// <summary>
        /// Rounds the ticks.
        /// </summary>
        /// <param name="ticks">The ticks.</param>
        /// <returns></returns>
        public static long RoundTicks(this long ticks)
        {
            //return ticks;
            return Convert.ToInt64((Convert.ToDouble(ticks) / 1000d)) * 1000;
        }

        /// <summary>
        /// Rounds the seconds to 4 decimals.
        /// </summary>
        /// <param name="seconds">The seconds.</param>
        /// <returns></returns>
        public static decimal RoundSeconds(this decimal seconds)
        {
            //return seconds;
            return Math.Round(seconds, 4);
        }

        #endregion

        #region Registration

        /// <summary>
        /// Extracts the FFmpeg Dlls.
        /// </summary>
        /// <param name="resourcePrefix">The resource prefix.</param>
        /// <returns></returns>
        private static string ExtractFFmpegDlls(string resourcePrefix)
        {
            var assembly = typeof(Utils).Assembly;
            var resourceNames = assembly.GetManifestResourceNames().Where(r => r.Contains(resourcePrefix)).ToArray();
            var targetDirectory = Path.Combine(Path.GetTempPath(), assembly.GetName().Name, assembly.GetName().Version.ToString(), resourcePrefix);

            if (Directory.Exists(targetDirectory) == false)
                Directory.CreateDirectory(targetDirectory);

            foreach (var dllResourceName in resourceNames)
            {
                var dllFilenameParts = dllResourceName.Split(new string[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                var dllFilename = dllFilenameParts[dllFilenameParts.Length - 2] + "." + dllFilenameParts[dllFilenameParts.Length - 1];
                var targetFileName = Path.Combine(targetDirectory, dllFilename);

                if (File.Exists(targetFileName))
                    continue;

                byte[] dllContents = null;

                // read the contents of the resource into a byte array
                using (var stream = assembly.GetManifestResourceStream(dllResourceName))
                {
                    dllContents = new byte[(int)stream.Length];
                    stream.Read(dllContents, 0, Convert.ToInt32(stream.Length));
                }

                // check the hash and overwrite the file if the file does not exist.
                File.WriteAllBytes(targetFileName, dllContents);

            }

            // This now holds the name of the temp directory where files got extracted.
            var directoryInfo = new System.IO.DirectoryInfo(targetDirectory);
            return directoryInfo.FullName;
        }

        /// <summary>
        /// Gets the assembly location.
        /// </summary>
        /// <value>
        /// The assembly location.
        /// </value>
        private static string AssemblyLocation
        {
            get
            {
                return Path.GetFullPath(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location));
            }
        }

        /// <summary>
        /// Registers FFmpeg library and initializes its components.
        /// It only needs to be called once but calling it more than once
        /// has no effect.
        /// </summary>
        /// <exception cref="System.BadImageFormatException"></exception>
        public static void RegisterFFmpeg()
        {
            lock (FFmpegRegisterLock)
            {
                if (HasFFmpegRegistered)
                    return;

                var resourceFolderName = string.Empty;
                var assemblyMachineType = typeof(Utils).Assembly.GetName().ProcessorArchitecture;
                if (assemblyMachineType == ProcessorArchitecture.X86 || assemblyMachineType == ProcessorArchitecture.MSIL || assemblyMachineType == ProcessorArchitecture.Amd64)
                    resourceFolderName = "ffmpeg32";
                else
                    throw new BadImageFormatException(
                        string.Format("Cannot load FFmpeg for architecture '{0}'", assemblyMachineType.ToString()));

                Paths.BasePath = ExtractFFmpegDlls(resourceFolderName);
                Paths.FFmpeg = Path.Combine(Paths.BasePath, "ffmpeg.exe");
                Paths.FFplay = Path.Combine(Paths.BasePath, "ffplay.exe");
                Paths.FFprobe = Path.Combine(Paths.BasePath, "ffprobe.exe");

                SetDllDirectory(Paths.BasePath);

                ffmpeg.av_log_set_flags(ffmpeg.AV_LOG_SKIP_REPEATED);

                ffmpeg.avdevice_register_all();
                ffmpeg.av_register_all();
                ffmpeg.avcodec_register_all();
                ffmpeg.avformat_network_init();

                HasFFmpegRegistered = true;
            }

        }

        #endregion

        #region Misc

        /// <summary>
        /// Determines if we are currently in Design Time
        /// </summary>
        /// <value>
        /// <c>true</c> if this instance is in design time; otherwise, <c>false</c>.
        /// </value>
        public static bool IsInDesignTime
        {
            get
            {
                if (!isInDesignTime.HasValue)
                {
                    isInDesignTime = (bool)DesignerProperties.IsInDesignModeProperty.GetMetadata(
                          typeof(DependencyObject)).DefaultValue;
                }
                return isInDesignTime.Value;
            }
        }

        #endregion
    }

}
