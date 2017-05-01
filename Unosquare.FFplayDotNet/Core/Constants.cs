using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Core
{
    /// <summary>
    /// Contains a set of internal constants and definitions
    /// </summary>
    static internal class Constants
    {

        public const int SDL_AUDIO_MIN_BUFFER_SIZE = 512;
        public const int SDL_AUDIO_MAX_CALLBACKS_PER_SEC = 30;
        public const int SDL_VOLUME_STEP = (SDL.SDL_MIX_MAXVOLUME / 50);

        public const AVPixelFormat OutputPixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;
        public const int OutputPixelFormatBpp = 4;

        public const int MinFrames = 25;
        public const int ExternalClockMinFrames = 2;
        public const int ExternalClockMaxFrames = 10;

        public const int AVMEDIA_TYPE_COUNT = (int)AVMediaType.AVMEDIA_TYPE_NB;

        /// <summary>
        /// No AV sync correction is done if below the minimum AV sync threshold.
        /// Port of AV_SYNC_THRESHOLD_MIN 
        /// </summary>
        public const double AvSyncThresholdMinSecs = 0.04;

        /// <summary>
        /// AV sync correction is done if above the maximum AV sync threshold.
        /// Port of AV_SYNC_THRESHOLD_MAX 
        /// </summary>
        public const double AvSyncThresholdMaxSecs = 0.1;

        /// <summary>
        /// If a frame duration is longer than this, it will not be duplicated to compensate AV sync.
        /// Port of AV_SYNC_FRAMEDUP_THRESHOLD 
        /// </summary>
        public const double AvSuncFrameDupThresholdSecs = 0.1;

        /// <summary>
        /// No AV correction is done if too big error. Unit is Seconds.
        /// Port of AV_NOSYNC_THRESHOLD
        /// </summary>
        public const double AvNoSyncThresholdSecs = 10.0;

        public const int SampleCorrectionPercentMax = 10;

        public const double ExternalClockSpeedMin = 0.900;
        public const double ExternalClockSpeedMax = 1.010;
        public const double ExternalClockSpeedStep = 0.001;

        /// <summary>
        /// The audio skew minimum samples
        /// Port of AUDIO_DIFF_AVG_NB
        /// </summary>
        public const int AudioSkewMinSamples = 20;

        /// <summary>
        /// The refresh rate in seconds (10 milliseconds = 0.01 seoconds).
        /// Port of REFRESH_RATE
        /// </summary>
        public const double RefreshRateSeconds = 0.01;

        public const int VideoQueueSize = 3;
        public const int SubtitleQueueSize = 16;
        public const int SampleQueueSize = 9;
        public static readonly int FrameQueueSize = Math.Max(SampleQueueSize, Math.Max(VideoQueueSize, SubtitleQueueSize));

    }
}
