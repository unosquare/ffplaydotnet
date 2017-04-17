using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet
{
    partial class FFplay
    {
        #region Constant Definitions

        internal const int AVERROR_NOTSUPP = -40;
        internal const int MAX_QUEUE_SIZE = (15 * 1024 * 1024);
        internal const int MIN_FRAMES = 25;
        internal const int EXTERNAL_CLOCK_MIN_FRAMES = 2;
        internal const int EXTERNAL_CLOCK_MAX_FRAMES = 10;
        internal const int SDL_AUDIO_MIN_BUFFER_SIZE = 512;
        internal const int SDL_AUDIO_MAX_CALLBACKS_PER_SEC = 30;
        internal const int SDL_VOLUME_STEP = (SDL_MIX_MAXVOLUME / 50);
        internal const double AV_SYNC_THRESHOLD_MIN = 0.04;
        internal const double AV_SYNC_THRESHOLD_MAX = 0.1;
        internal const double AV_SYNC_FRAMEDUP_THRESHOLD = 0.1;
        internal const double AV_NOSYNC_THRESHOLD = 10.0;
        internal const int SAMPLE_CORRECTION_PERCENT_MAX = 10;
        internal const double EXTERNAL_CLOCK_SPEED_MIN = 0.900;
        internal const double EXTERNAL_CLOCK_SPEED_MAX = 1.010;
        internal const double EXTERNAL_CLOCK_SPEED_STEP = 0.001;
        internal const int AUDIO_DIFF_AVG_NB = 20;
        internal const double REFRESH_RATE = 0.01;
        internal const int SAMPLE_ARRAY_SIZE = (8 * 65536);
        internal const int CURSOR_HIDE_DELAY = 1000000;
        internal const int USE_ONEPASS_SUBTITLE_RENDER = 1;
        internal const int VIDEO_PICTURE_QUEUE_SIZE = 3;
        internal const int SUBPICTURE_QUEUE_SIZE = 16;
        internal const int SAMPLE_QUEUE_SIZE = 9;
        internal static readonly int FRAME_QUEUE_SIZE = Math.Max(SAMPLE_QUEUE_SIZE, Math.Max(VIDEO_PICTURE_QUEUE_SIZE, SUBPICTURE_QUEUE_SIZE));
        internal const int FF_ALLOC_EVENT = (SDL_USEREVENT);
        internal const int FF_QUIT_EVENT = (SDL_USEREVENT + 2);

        #endregion

    }
}
