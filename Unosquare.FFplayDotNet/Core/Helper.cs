namespace Unosquare.FFplayDotNet.Core
{
    using FFmpeg.AutoGen;
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// Provides methods and constants for miscellaneous operations
    /// </summary>
    internal static class Helper
    {
        static private bool HasRegistered = false;
        static private bool? designTime;

        /// <summary>
        /// The register synchronization lock
        /// </summary>
        static private readonly object RegisterLock = new object();

        /// <summary>
        /// Extracts the FFmpeg Dlls.
        /// </summary>
        /// <param name="resourcePrefix">The resource prefix.</param>
        /// <returns></returns>
        private static string ExtractFFmpegDlls(string resourcePrefix)
        {
            var assembly = typeof(Helper).Assembly;
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
            lock (RegisterLock)
            {
                if (HasRegistered)
                    return;

                var resourceFolderName = string.Empty;
                var assemblyMachineType = typeof(Helper).Assembly.GetName().ProcessorArchitecture;
                if (assemblyMachineType == ProcessorArchitecture.X86 || assemblyMachineType == ProcessorArchitecture.MSIL || assemblyMachineType == ProcessorArchitecture.Amd64)
                    resourceFolderName = "ffmpeg32";
                else
                    throw new BadImageFormatException(
                        string.Format("Cannot load FFmpeg for architecture '{0}'", assemblyMachineType.ToString()));

                Paths.BasePath = ExtractFFmpegDlls(resourceFolderName);
                Paths.FFmpeg = Path.Combine(Paths.BasePath, "ffmpeg.exe");
                Paths.FFplay = Path.Combine(Paths.BasePath, "ffplay.exe");
                Paths.FFprobe = Path.Combine(Paths.BasePath, "ffprobe.exe");

                Native.SetDllDirectory(Paths.BasePath);

                ffmpeg.av_log_set_flags(ffmpeg.AV_LOG_SKIP_REPEATED);

                ffmpeg.avdevice_register_all();
                ffmpeg.av_register_all();
                //ffmpeg.avformat_network_init(); // this shall be called before opening a stream and deinit when closing the streams.
                ffmpeg.avcodec_register_all();

                HasRegistered = true;
            }

        }

        /// <summary>
        /// Determines whether [is no PTS value].
        /// </summary>
        /// <param name="timestamp">The timestamp.</param>
        /// <returns>
        ///   <c>true</c> if [is no PTS value] [the specified timestamp]; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNoPtsValue(this long timestamp)
        {
            return Convert.ToDouble(timestamp) == -Convert.ToDouble(0x8000000000000000L);
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
                return false;
                /*
                if (!designTime.HasValue)
                {
                    designTime = (bool)DesignerProperties.IsInDesignModeProperty.GetMetadata(
                          typeof(DependencyObject)).DefaultValue;
                }
                return designTime.Value;
                */
            }
        }

        /// <summary>
        /// Converts a Timestamp to seconds.
        /// </summary>
        /// <param name="timestamp">The ts.</param>
        /// <param name="streamTimebase">The stream time base.</param>
        /// <returns></returns>
        public static decimal TimestampToSeconds(this long timestamp, AVRational streamTimebase)
        {
            return Convert.ToDecimal(Convert.ToDouble(timestamp) * Convert.ToDouble(streamTimebase.num) / Convert.ToDouble(streamTimebase.den));
        }

        /// <summary>
        /// Converts seconds to a timestamp value.
        /// </summary>
        /// <param name="seconds">The seconds.</param>
        /// <param name="streamTimebase">The stream time base.</param>
        /// <returns></returns>
        public static long SecondsToTimestamp(this decimal seconds, AVRational streamTimebase)
        {
            return Convert.ToInt64(Convert.ToDouble(seconds) * Convert.ToDouble(streamTimebase.den) / Convert.ToDouble(streamTimebase.num));
        }

        /// <summary>
        /// Converts a timestamp to a timespan
        /// </summary>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="streamTimebase">The stream timebase.</param>
        /// <returns></returns>
        public static TimeSpan TmestampToTimeSpan(this long timestamp, int streamTimebase)
        {
            var totalSeconds = (double)timestamp / streamTimebase;
            return TimeSpan.FromSeconds(totalSeconds);
        }

        /// <summary>
        /// Converts a timestamp to a timespan
        /// </summary>
        /// <param name="timestamp">The timestamp.</param>
        /// <returns></returns>
        public static TimeSpan TmestampToTimeSpan(this long timestamp)
        {
            return TmestampToTimeSpan(timestamp, ffmpeg.AV_TIME_BASE);
        }

        /// <summary>
        /// Gets the FFmpeg error mesage based on the error code
        /// </summary>
        /// <param name="code">The code.</param>
        /// <returns></returns>
        public static unsafe string GetFFmpegErrorMessage(int code)
        {
            var errorStrBytes = new byte[1024];
            var errorStrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(byte)) * errorStrBytes.Length);
            ffmpeg.av_strerror(code, (byte*)errorStrPtr, (ulong)errorStrBytes.Length);
            Marshal.Copy(errorStrPtr, errorStrBytes, 0, errorStrBytes.Length);
            Marshal.FreeHGlobal(errorStrPtr);

            var errorMessage = Encoding.GetEncoding(0).GetString(errorStrBytes).Split('\0').FirstOrDefault();
            return errorMessage;
        }


        /// <summary>
        /// Checks the syntax of the stream specifier.
        /// Port of check_stream_specifier
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="stream">The stream.</param>
        /// <param name="streamSpec">The spec.</param>
        /// <returns></returns>
        unsafe public static int ValidateStreamSpecifier(AVFormatContext* format, AVStream* stream, string streamSpec)
        {
            var result = ffmpeg.avformat_match_stream_specifier(format, stream, streamSpec);
            if (result < 0)
                ffmpeg.av_log(format, ffmpeg.AV_LOG_ERROR, $"Invalid stream specifier: {streamSpec}.\n");

            return result;
        }

        /// <summary>
        /// Retrieves the Stream options dictionary.
        /// Port of setup_find_stream_info_opts.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="codecOptions">The codec options.</param>
        /// <returns></returns>
        unsafe public static AVDictionary** RetrieveStreamOptions(AVFormatContext* format, AVDictionary* codecOptions)
        {
            if (format->nb_streams == 0)
                return null;

            var orginalOpts = new AVDictionary();
            var optsReference = &orginalOpts;
            var streamOptions = &optsReference;

            for (var i = 0; i < format->nb_streams; i++)
                streamOptions[i] = FilterCodecOptions(codecOptions, format->streams[i]->codecpar->codec_id, format, format->streams[i], null);

            return streamOptions;
        }

        /// <summary>
        /// Retrieves a dictionary with the options for the specified codec.
        /// Port of filter_codec_opts
        /// </summary>
        /// <param name="allOptions">The input options.</param>
        /// <param name="codecId">The codec identifier.</param>
        /// <param name="format">The format.</param>
        /// <param name="stream">The stream.</param>
        /// <param name="codec">The codec.</param>
        /// <returns></returns>
        unsafe public static AVDictionary* FilterCodecOptions(AVDictionary* allOptions, AVCodecID codecId, AVFormatContext* format, AVStream* stream, AVCodec* codec)
        {
            // TODO: https://github.com/FFmpeg/FFmpeg/blob/d7896e9b4228e5b7ffc7ef0d0f1cf145f518c819/cmdutils.c#L2002

            AVDictionary* result = null;
            AVDictionaryEntry* currentEntry = null;
            var flags = format->oformat != null ?
                ffmpeg.AV_OPT_FLAG_ENCODING_PARAM : ffmpeg.AV_OPT_FLAG_DECODING_PARAM;

            var prefix = (char)0;
            var cc = ffmpeg.avcodec_get_class();

            if (codec == null)
                codec = (format->oformat != null) ?
                    ffmpeg.avcodec_find_encoder(codecId) : ffmpeg.avcodec_find_decoder(codecId);

            switch (stream->codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    prefix = 'v';
                    flags |= ffmpeg.AV_OPT_FLAG_VIDEO_PARAM;
                    break;
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    prefix = 'a';
                    flags |= ffmpeg.AV_OPT_FLAG_AUDIO_PARAM;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    prefix = 's';
                    flags |= ffmpeg.AV_OPT_FLAG_SUBTITLE_PARAM;
                    break;
            }

            //E.g. -codec:a:1 ac3 contains the a:1 stream specifier
            //E.g. the stream specifier in -b:a 128k
            while ((currentEntry = ffmpeg.av_dict_get(allOptions, "", currentEntry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Native.BytePtrToString(currentEntry->key);
                if (string.IsNullOrWhiteSpace(key)) continue;

                var value = Native.BytePtrToString(currentEntry->value);
                var keyParts = key.Split(new char[] { ':' }, 2);

                /* check stream specification in opt name */
                if (keyParts.Length > 1)
                {
                    switch (ValidateStreamSpecifier(format, stream, keyParts[1]))
                    {
                        case 1: key = keyParts[0]; break;
                        case 0: continue;
                        default: continue;
                    }
                }


                if (ffmpeg.av_opt_find(&cc, key, null, flags, ffmpeg.AV_OPT_SEARCH_FAKE_OBJ) != null ||
                    codec == null ||
                    (codec->priv_class != null &&
                     ffmpeg.av_opt_find(&codec->priv_class, key, null, flags, ffmpeg.AV_OPT_SEARCH_FAKE_OBJ) != null))
                {
                    ffmpeg.av_dict_set(&result, key, value, 0);
                }
                else if (key[0] == prefix && keyParts.Length > 1 && ffmpeg.av_opt_find(&cc, keyParts[1], null, flags, ffmpeg.AV_OPT_SEARCH_FAKE_OBJ) != null)
                {
                    ffmpeg.av_dict_set(&result, key + 1, value, 0);
                }
            }

            return result;
        }


        /// <summary>
        /// Gets a value indicating whether we are running windows
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is windows; otherwise, <c>false</c>.
        /// </value>
        public static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }
    }

}
