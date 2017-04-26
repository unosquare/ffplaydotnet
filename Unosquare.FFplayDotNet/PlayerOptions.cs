using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unosquare.FFplayDotNet.Core;

namespace Unosquare.FFplayDotNet
{
    public class PlayerOptions
    {

        /// <summary>
        /// Media synchronization mode.
        /// Port of av_sync_type
        /// </summary>
        public SyncMode MediaSyncMode { get; set; } = SyncMode.Audio; // Default is Audio

        /// <summary>
        /// The filename to open.
        /// Port of input_filename
        /// </summary>
        public string MediaInputUrl { get; set; }

        /// <summary>
        /// The name of the forced input format to use
        /// </summary>
        public string InputFormatName { get; set; } = null;

        /// <summary>
        /// Port of sws_flags
        /// </summary>
        public int VideoScalerFlags { get; set; } = ffmpeg.SWS_BICUBIC;

        /// <summary>
        /// Prevent reading from audio streams.
        /// Port of audio_disable
        /// </summary>
        public bool IsAudioDisabled { get; set; } = false;

        /// <summary>
        /// Prevent reading from video streams.
        /// Port of video_disable
        /// </summary>
        public bool IsVideoDisabled { get; set; } = false;

        /// <summary>
        /// Prevent reading from subtitle streams
        /// Port of subtitle_disable
        /// </summary>
        public bool IsSubtitleDisabled { get; set; } = false;

        public bool EnableFastDecoding { get; set; } = false;

        public bool GeneratePts { get; set; } = false;

        public bool EnableLowRes { get; set; } = false;

        /// <summary>
        /// Enable dropping frames when cpu is too slow.
        /// Port of framedrop
        /// </summary>
        public bool EnableFrameDrops { get; set; } = true;

        /// <summary>
        /// If set to > 0, don't limit the input buffer size (useful with realtime streams).
        /// Port of infinite_buffer
        /// </summary>
        internal int EnableInfiniteBuffer { get; set; } = -1;
    }
}
