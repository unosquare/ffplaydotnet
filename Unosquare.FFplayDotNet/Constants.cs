using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet
{
    /// <summary>
    /// Contains a set of internal constants and definitions
    /// </summary>
    static internal class Constants
    {

        public const int SDL_AUDIO_MIN_BUFFER_SIZE = 512;
        public const int SDL_AUDIO_MAX_CALLBACKS_PER_SEC = 30;
        public const int SDL_VOLUME_STEP = (SDL.SDL_MIX_MAXVOLUME / 50);

        public const int FF_ALLOC_EVENT = (SDL.SDL_USEREVENT);
        public const int FF_QUIT_EVENT = (SDL.SDL_USEREVENT + 2);

        
        public const int MinFrames = 25;
        public const int ExternalClockMinFrames = 2;
        public const int ExternalClockMaxFrames = 10;

        public const double AvSyncThresholdMin = 0.04;
        public const double AvSyncThresholdMax = 0.1;
        public const double AvSuncFrameDupThreshold = 0.1;
        public const double AvNoSyncThreshold = 10.0;
        public const int SampleCorrectionPercentMax = 10;

        public const double ExternalClockSpeedMin = 0.900;
        public const double ExternalClockSpeedMax = 1.010;
        public const double ExternalClockSpeedStep = 0.001;

        /// <summary>
        /// The audio skew minimum samples
        /// Port of AUDIO_DIFF_AVG_NB
        /// </summary>
        public const int AudioSkewMinSamples = 20;

        public const double RefreshRate = 0.01;

        public const int VideoQueueSize = 3;
        public const int SubtitleQueueSize = 16;
        public const int SampleQueueSize = 9;
        public static readonly int FrameQueueSize = Math.Max(SampleQueueSize, Math.Max(VideoQueueSize, SubtitleQueueSize));

    }
}
