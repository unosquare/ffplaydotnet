using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static Unosquare.FFplayDotNet.SDL;

namespace Unosquare.FFplayDotNet
{

    public unsafe class MediaState
    {
        internal readonly GCHandle Handle;

        internal AVInputFormat* InputFormat;
        internal AVFormatContext* InputContext;

        internal AVStream* AudioStream;
        internal AVStream* SubtitleStream;
        internal AVStream* VideoStream;

        internal SwrContext* AudioScaler;
        internal SwsContext* VideoScaler;
        internal SwsContext* SubtitleScaler;

        public SDL_Thread ReadThread;

        public bool IsAbortRequested { get; internal set; }
        public bool IsForceRefreshRequested { get; internal set; }
        public bool IsPaused { get; set; }

        public bool last_paused;

        public bool queue_attachments_req;
        public bool IsSeekRequested { get; internal set; }

        public int seek_flags;
        public long seek_pos;
        public long seek_rel;
        internal int ReadPauseResult;



        public bool IsMediaRealtime { get; internal set; }

        public Clock AudioClock { get; internal set; }
        public Clock VideoClock { get; internal set; }
        public Clock ExternalClock { get; internal set; }

        public FrameQueue VideoQueue { get; internal set; }
        public FrameQueue SubtitleQueue { get; internal set; }
        public FrameQueue AudioQueue { get; internal set; }

        public Decoder AudioDecoder { get; internal set; }
        public Decoder VideoDecoder { get; internal set; }
        public Decoder SubtitleDecoder { get; internal set; }

        public int AudioStreamIndex { get; set; }
        public int VideoStreamIndex { get; internal set; }
        public int SubtitleStreamIndex { get; internal set; }

        public SyncMode MediaSyncMode { get; set; }
        public SyncMode MasterSyncMode
        {
            get
            {
                if (MediaSyncMode == SyncMode.AV_SYNC_VIDEO_MASTER)
                {
                    if (VideoStream != null)
                        return SyncMode.AV_SYNC_VIDEO_MASTER;
                    else
                        return SyncMode.AV_SYNC_AUDIO_MASTER;
                }
                else if (MediaSyncMode == SyncMode.AV_SYNC_AUDIO_MASTER)
                {
                    if (AudioStream != null)
                        return SyncMode.AV_SYNC_AUDIO_MASTER;
                    else
                        return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
                }
                else
                {
                    return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
                }
            }
        }

        public double MasterClockPosition
        {
            get
            {
                double val;
                switch (MasterSyncMode)
                {
                    case SyncMode.AV_SYNC_VIDEO_MASTER:
                        val = VideoClock.Position;
                        break;
                    case SyncMode.AV_SYNC_AUDIO_MASTER:
                        val = AudioClock.Position;
                        break;
                    default:
                        val = ExternalClock.Position;
                        break;
                }
                return val;

            }
        }
        public double AudioClockPosition { get; internal set; }
        public int AudioClockSerial { get; internal set; }

        public double audio_diff_cum; /* used for AV difference average computation */
        public double audio_diff_avg_coef;
        public double audio_diff_threshold;
        public int audio_diff_avg_count;

        internal PacketQueue VideoPackets { get; } = new PacketQueue();
        internal PacketQueue AudioPackets { get; } = new PacketQueue();
        internal PacketQueue SubtitlePackets { get; } = new PacketQueue();

        public AudioParams AudioInputParams { get; } = new AudioParams();
        public AudioParams AudioOutputParams { get; } = new AudioParams();
        public int AudioVolume { get; set; }
        public bool IsAudioMuted { get; set; }
        public int AudioHardwareBufferSize { get; internal set; }

        public byte* audio_buf;
        public byte* audio_buf1;
        public uint audio_buf_size; /* in bytes */
        public uint audio_buf1_size;
        public int audio_buf_index; /* in bytes */
        public int audio_write_buf_size;

        public int frame_drops_early;
        public int frame_drops_late;


        public int xpos;
        public double last_vis_time;
        public SDL_Texture vis_texture;
        public SDL_Texture sub_texture;

        public double frame_timer;
        public double frame_last_returned_time;
        public double frame_last_filter_delay;

        /// <summary>
        /// Gets the maximum duration of the frame.
        /// above this, we consider the jump a timestamp discontinuity
        /// </summary>
        public double MaximumFrameDuration { get; internal set; }

        public bool IsAtEndOfFile { get; internal set; }
        public string MediaUrl { get; internal set; }
        public int PictureWidth { get; internal set; }
        public int PictureHeight { get; internal set; }
        public int xleft;
        public int ytop;
        public bool IsFrameStepping { get; internal set; }

        public int last_video_stream, last_audio_stream, last_subtitle_stream;

        public FFplay Player { get; private set; }

        public SDL_cond continue_read_thread;

        public MediaState(FFplay player, string filename, AVInputFormat* iformat)
        {
            Handle = GCHandle.Alloc(this, GCHandleType.Pinned);

            Player = player;
            MediaUrl = filename;
            InputFormat = iformat;
            ytop = 0;
            xleft = 0;

            VideoQueue = new FrameQueue(VideoPackets, Constants.VideoQueueSize, true);
            SubtitleQueue = new FrameQueue(SubtitlePackets, Constants.SubtitleQueueSize, false);
            AudioQueue = new FrameQueue(AudioPackets, Constants.SampleQueueSize, true);

            continue_read_thread = SDL_CreateCond();

            VideoClock = new Clock(() => { return new int?(VideoPackets.Serial); });
            AudioClock = new Clock(() => { return new int?(AudioPackets.Serial); });
            ExternalClock = new Clock(() => { return new int?(ExternalClock.PacketSerial); });

            AudioClockSerial = -1;
            AudioVolume = SDL_MIX_MAXVOLUME;
            IsAudioMuted = false;
            MediaSyncMode = MediaSyncMode;
            ReadThread = SDL_CreateThread(player.read_thread, this);

        }

        public void AdjustExternalClockSpeedRatio()
        {
            if (VideoStreamIndex >= 0 && VideoPackets.Length <= Constants.ExternalClockMinFrame ||
                AudioStreamIndex >= 0 && AudioPackets.Length <= Constants.ExternalClockMinFrame)
            {
                ExternalClock.SpeedRatio = (Math.Max(Constants.ExternalClockSpeedMin, ExternalClock.SpeedRatio - Constants.ExternalClockSpeedStep));
            }
            else if ((VideoStreamIndex < 0 || VideoPackets.Length > Constants.ExternalClockMaxFrames) &&
                     (AudioStreamIndex < 0 || AudioPackets.Length > Constants.ExternalClockMaxFrames))
            {
                ExternalClock.SpeedRatio = (Math.Min(Constants.ExternalClockSpeedMax, ExternalClock.SpeedRatio + Constants.ExternalClockSpeedStep));
            }
            else
            {
                var speedRatio = ExternalClock.SpeedRatio;
                if (speedRatio != 1.0)
                    ExternalClock.SpeedRatio = (speedRatio + Constants.ExternalClockSpeedStep * (1.0 - speedRatio) / Math.Abs(1.0 - speedRatio));
            }
        }

        public int EnqueuePicture(AVFrame* sourceFrame, double pts, double duration)
        {
            var vp = VideoQueue.PeekWritableFrame();
            var serial = VideoDecoder.PacketSerial;
            var streamPosition = ffmpeg.av_frame_get_pkt_pos(sourceFrame);

            Debug.WriteLine($"frame_type={ffmpeg.av_get_picture_type_char(sourceFrame->pict_type)} pts={pts}");

            if (vp == null)
                return -1;

            vp.PictureAspectRatio = sourceFrame->sample_aspect_ratio;
            vp.IsUploaded = false;

            if (vp.bmp == null || !vp.IsAllocated ||
                vp.PictureWidth != sourceFrame->width ||
                vp.PictureHeight != sourceFrame->height ||
                vp.format != sourceFrame->format)
            {
                var ev = new SDL_Event();
                vp.IsAllocated = false;
                vp.PictureWidth = sourceFrame->width;
                vp.PictureHeight = sourceFrame->height;
                vp.format = sourceFrame->format;
                ev.type = Constants.FF_ALLOC_EVENT;
                ev.user_data1 = this;
                SDL_PushEvent(ev);
                SDL_LockMutex(VideoQueue.mutex);

                while (!vp.IsAllocated && !VideoPackets.IsAborted)
                {
                    SDL_CondWait(VideoQueue.cond, VideoQueue.mutex);
                }
                if (VideoPackets.IsAborted && SDL_PeepEvents(ev, 1, SDL_GETEVENT, Constants.FF_ALLOC_EVENT, Constants.FF_ALLOC_EVENT) != 1)
                {
                    while (!vp.IsAllocated && !IsAbortRequested)
                    {
                        SDL_CondWait(VideoQueue.cond, VideoQueue.mutex);
                    }
                }
                SDL_UnlockMutex(VideoQueue.mutex);
                if (VideoPackets.IsAborted)
                    return -1;
            }

            if (vp.bmp != null)
            {
                vp.Pts = pts;
                vp.EstimatedDuration = duration;
                vp.BytePosition = streamPosition;
                vp.Serial = serial;
                ffmpeg.av_frame_move_ref(vp.DecodedFrame, sourceFrame);
                VideoQueue.frame_queue_push();
            }
            return 0;
        }

        public void SeekTo(long pos, long rel, bool seekByBytes)
        {
            if (IsSeekRequested) return;

            seek_pos = pos;
            seek_rel = rel;
            seek_flags &= ~ffmpeg.AVSEEK_FLAG_BYTE;
            if (seekByBytes) seek_flags |= ffmpeg.AVSEEK_FLAG_BYTE;
            IsSeekRequested = true;
            SDL_CondSignal(continue_read_thread);
        }

        public void SeekChapter(int increment)
        {
            long pos = Convert.ToInt64(MasterClockPosition * ffmpeg.AV_TIME_BASE);
            int i = 0;

            if (InputContext->nb_chapters == 0)
                return;

            for (i = 0; i < InputContext->nb_chapters; i++)
            {
                AVChapter* ch = InputContext->chapters[i];
                if (ffmpeg.av_compare_ts(pos, ffmpeg.AV_TIME_BASE_Q, ch->start, ch->time_base) < 0)
                {
                    i--;
                    break;
                }
            }

            i += increment;
            i = Math.Max(i, 0);
            if (i >= InputContext->nb_chapters)
                return;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Seeking to chapter {i}.\n");
            SeekTo(ffmpeg.av_rescale_q(InputContext->chapters[i]->start, InputContext->chapters[i]->time_base,
                                         ffmpeg.AV_TIME_BASE_Q), 0, false);
        }

        public void stream_cycle_channel(AVMediaType codec_type)
        {
            var ic = InputContext;
            int start_index, stream_index;
            int old_index;
            AVStream* st;
            AVProgram* p = null;

            int nb_streams = (int)InputContext->nb_streams;

            if (codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                start_index = last_video_stream;
                old_index = VideoStreamIndex;
            }
            else if (codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                start_index = last_audio_stream;
                old_index = AudioStreamIndex;
            }
            else
            {
                start_index = last_subtitle_stream;
                old_index = SubtitleStreamIndex;
            }

            stream_index = start_index;
            if (codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO && VideoStreamIndex != -1)
            {
                p = ffmpeg.av_find_program_from_stream(ic, null, VideoStreamIndex);
                if (p != null)
                {
                    nb_streams = (int)p->nb_stream_indexes;
                    for (start_index = 0; start_index < nb_streams; start_index++)
                        if (p->stream_index[start_index] == stream_index)
                            break;
                    if (start_index == nb_streams)
                        start_index = -1;
                    stream_index = start_index;
                }
            }

            while (true)
            {
                if (++stream_index >= nb_streams)
                {
                    if (codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    {
                        stream_index = -1;
                        last_subtitle_stream = -1;
                        goto the_end;
                    }
                    if (start_index == -1)
                        return;
                    stream_index = 0;
                }
                if (stream_index == start_index)
                    return;
                st = InputContext->streams[p != null ? (int)p->stream_index[stream_index] : stream_index];
                if (st->codecpar->codec_type == codec_type)
                {
                    switch (codec_type)
                    {
                        case AVMediaType.AVMEDIA_TYPE_AUDIO:
                            if (st->codecpar->sample_rate != 0 &&
                                st->codecpar->channels != 0)
                                goto the_end;
                            break;
                        case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                            goto the_end;
                        default:
                            break;
                    }
                }
            }
            the_end:
            if (p != null && stream_index != -1)
                stream_index = (int)p->stream_index[stream_index];
            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, $"Switch {ffmpeg.av_get_media_type_string(codec_type)} stream from #{old_index} to #{stream_index}\n");
            stream_component_close(old_index);
            OpenStreamComponent(stream_index);
        }

        public void stream_component_close(int stream_index)
        {
            var ic = InputContext;
            AVCodecParameters* codecpar;

            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return;

            codecpar = ic->streams[stream_index]->codecpar;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    AudioDecoder.DecoderAbort(AudioQueue);
                    SDL_CloseAudio();
                    AudioDecoder.DecoderDestroy();
                    fixed (SwrContext** vst_swr_ctx = &AudioScaler)
                    {
                        ffmpeg.swr_free(vst_swr_ctx);
                    }

                    ffmpeg.av_freep((void*)audio_buf1);
                    audio_buf1_size = 0;
                    audio_buf = null;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoDecoder.DecoderAbort(VideoQueue);
                    VideoDecoder.DecoderDestroy();
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleDecoder.DecoderAbort(SubtitleQueue);
                    SubtitleDecoder.DecoderDestroy();
                    break;
                default:
                    break;
            }

            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_ALL;

            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    AudioStream = null;
                    AudioStreamIndex = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoStream = null;
                    VideoStreamIndex = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleStream = null;
                    SubtitleStreamIndex = -1;
                    break;
                default:
                    break;
            }
        }

        public int OpenStreamComponent(int streamIndex)
        {
            var ic = InputContext;
            string forcedCodecName = null;
            AVDictionaryEntry* kvp = null;

            int sample_rate = 0;
            int channelCount = 0;
            long channelLayout = 0;
            int result = 0;

            int stream_lowres = Convert.ToInt32(Player.EnableLowRes);
            if (streamIndex < 0 || streamIndex >= ic->nb_streams)
                return -1;

            var avctx = ffmpeg.avcodec_alloc_context3(null);
            if (avctx == null)
                return ffmpeg.AVERROR_ENOMEM;

            result = ffmpeg.avcodec_parameters_to_context(avctx, ic->streams[streamIndex]->codecpar);
            if (result < 0) goto fail;
            ffmpeg.av_codec_set_pkt_timebase(avctx, ic->streams[streamIndex]->time_base);
            var codec = ffmpeg.avcodec_find_decoder(avctx->codec_id);
            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO: last_audio_stream = streamIndex; forcedCodecName = Player.AudioCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: last_subtitle_stream = streamIndex; forcedCodecName = Player.SubtitleCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO: last_video_stream = streamIndex; forcedCodecName = Player.VideoCodecName; break;
            }
            if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
                codec = ffmpeg.avcodec_find_decoder_by_name(forcedCodecName);

            if (codec == null)
            {
                if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with name '{forcedCodecName}'\n");
                else
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with id {avctx->codec_id}\n");

                result = ffmpeg.AVERROR_EINVAL;
                goto fail;
            }

            avctx->codec_id = codec->id;

            if (stream_lowres > ffmpeg.av_codec_get_max_lowres(codec))
            {
                ffmpeg.av_log(avctx, ffmpeg.AV_LOG_WARNING, $"The maximum value for lowres supported by the decoder is {ffmpeg.av_codec_get_max_lowres(codec)}\n");
                stream_lowres = ffmpeg.av_codec_get_max_lowres(codec);
            }

            ffmpeg.av_codec_set_lowres(avctx, stream_lowres);

            if (stream_lowres != 0)
                avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            if (Player.EnableFastDecoding)
                avctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            var opts = FFplay.filter_codec_opts(Player.CodecOptions, avctx->codec_id, ic, ic->streams[streamIndex], codec);

            if (ffmpeg.av_dict_get(opts, "threads", null, 0) == null)
                ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            if (stream_lowres != 0)
                ffmpeg.av_dict_set_int(&opts, "lowres", stream_lowres, 0);

            if (avctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || avctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                ffmpeg.av_dict_set(&opts, "refcounted_frames", "1", 0);

            if ((result = ffmpeg.avcodec_open2(avctx, codec, &opts)) < 0)
            {
                goto fail;
            }

            if ((kvp = ffmpeg.av_dict_get(opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Marshal.PtrToStringAnsi(new IntPtr(kvp->key))} not found.\n");
                result = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            IsAtEndOfFile = false;
            ic->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;

            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    if ((result = Player.audio_open(this, channelLayout, channelCount, sample_rate, AudioOutputParams)) < 0)
                        goto fail;

                    AudioHardwareBufferSize = result;
                    AudioOutputParams.CopyTo(AudioInputParams);
                    audio_buf_size = 0;
                    audio_buf_index = 0;
                    audio_diff_avg_coef = Math.Exp(Math.Log(0.01) / Constants.AUDIO_DIFF_AVG_NB);
                    audio_diff_avg_count = 0;
                    audio_diff_threshold = (double)(AudioHardwareBufferSize) / AudioOutputParams.BytesPerSecond;
                    AudioStreamIndex = streamIndex;
                    AudioStream = ic->streams[streamIndex];

                    AudioDecoder = new Decoder(avctx, AudioPackets, continue_read_thread);

                    if ((InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                        InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
                    {
                        AudioDecoder.StartPts = AudioStream->start_time;
                        AudioDecoder.StartPtsTimebase = AudioStream->time_base;
                    }

                    if ((result = Player.decoder_start(AudioDecoder, Player.audio_thread, this)) < 0)
                        goto final;

                    SDL_PauseAudio(0);
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoStreamIndex = streamIndex;
                    VideoStream = ic->streams[streamIndex];
                    VideoDecoder = new Decoder(avctx, VideoPackets, continue_read_thread);
                    if ((result = Player.decoder_start(VideoDecoder, Player.video_thread, this)) < 0)
                        goto final;
                    queue_attachments_req = true;
                    break;

                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleStreamIndex = streamIndex;
                    SubtitleStream = ic->streams[streamIndex];
                    SubtitleDecoder = new Decoder(avctx, SubtitlePackets, continue_read_thread);
                    if ((result = Player.decoder_start(SubtitleDecoder, Player.subtitle_thread, this)) < 0)
                        goto final;
                    break;

                default:
                    break;
            }
            goto final;
            fail:
            ffmpeg.avcodec_free_context(&avctx);
            final:
            ffmpeg.av_dict_free(&opts);
            return result;
        }

        public void stream_close()
        {
            IsAbortRequested = true;
            SDL_WaitThread(ReadThread, null);
            if (AudioStreamIndex >= 0)
                stream_component_close(AudioStreamIndex);

            if (VideoStreamIndex >= 0)
                stream_component_close(VideoStreamIndex);

            if (SubtitleStreamIndex >= 0)
                stream_component_close(SubtitleStreamIndex);

            fixed (AVFormatContext** vstic = &InputContext)
            {
                ffmpeg.avformat_close_input(vstic);
            }

            VideoPackets.Clear();
            AudioPackets.Clear();
            SubtitlePackets.Clear();
            VideoQueue.frame_queue_destory();
            AudioQueue.frame_queue_destory();
            SubtitleQueue.frame_queue_destory();
            SDL_DestroyCond(continue_read_thread);
            ffmpeg.sws_freeContext(VideoScaler);
            ffmpeg.sws_freeContext(SubtitleScaler);

            if (vis_texture != null)
                SDL_DestroyTexture(vis_texture);
            if (sub_texture != null)
                SDL_DestroyTexture(sub_texture);
        }

        public double ComputeVideoClockDelay(double delay)
        {
            double syncThreshold = 0;
            double skew = 0;

            if (MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                skew = VideoClock.Position - MasterClockPosition;
                syncThreshold = Math.Max(
                    Constants.AvSyncThresholdMin,
                    Math.Min(Constants.AvSyncThresholdMax, delay));

                if (!double.IsNaN(skew) && Math.Abs(skew) < MaximumFrameDuration)
                {
                    if (skew <= -syncThreshold)
                        delay = Math.Max(0, delay + skew);
                    else if (skew >= syncThreshold && delay > Constants.AvSuncFrameDupThreshold)
                        delay = delay + skew;
                    else if (skew >= syncThreshold)
                        delay = 2 * delay;
                }
            }

            ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"video: delay={delay} A-V={-skew}\n");
            return delay;
        }

        public double ComputeFrameDuration(FrameHolder videoFrame, FrameHolder nextVideoFrame)
        {
            if (videoFrame.Serial == nextVideoFrame.Serial)
            {
                var duration = nextVideoFrame.Pts - videoFrame.Pts;
                if (double.IsNaN(duration) || duration <= 0 || duration > MaximumFrameDuration)
                    return videoFrame.EstimatedDuration;
                else
                    return duration;
            }
            else
            {
                return 0.0;
            }
        }

        public void video_image_display()
        {
            var vp = new FrameHolder();
            FrameHolder sp = null;
            var rect = new SDL_Rect();

            vp = VideoQueue.PeekLast();
            if (vp.bmp != null)
            {
                if (SubtitleStream != null)
                {
                    if (SubtitleQueue.PendingCount > 0)
                    {
                        sp = SubtitleQueue.Peek();
                        if (vp.Pts >= sp.Pts + ((float)sp.Subtitle.start_display_time / 1000))
                        {
                            if (!sp.IsUploaded)
                            {
                                byte** pixels = null;
                                int pitch = 0;

                                if (sp.PictureWidth == 0 || sp.PictureHeight == 0)
                                {
                                    sp.PictureWidth = vp.PictureWidth;
                                    sp.PictureHeight = vp.PictureHeight;
                                }

                                if (FFplay.realloc_texture(sub_texture, SDL_PIXELFORMAT_ARGB8888, sp.PictureWidth, sp.PictureHeight, SDL_BLENDMODE_BLEND, 1) < 0)
                                    return;

                                for (var i = 0; i < sp.Subtitle.num_rects; i++)
                                {
                                    AVSubtitleRect* sub_rect = sp.Subtitle.rects[i];
                                    sub_rect->x = ffmpeg.av_clip(sub_rect->x, 0, sp.PictureWidth);
                                    sub_rect->y = ffmpeg.av_clip(sub_rect->y, 0, sp.PictureHeight);
                                    sub_rect->w = ffmpeg.av_clip(sub_rect->w, 0, sp.PictureWidth - sub_rect->x);
                                    sub_rect->h = ffmpeg.av_clip(sub_rect->h, 0, sp.PictureHeight - sub_rect->y);

                                    SubtitleScaler = ffmpeg.sws_getCachedContext(SubtitleScaler,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_PAL8,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_BGRA,
                                        0, null, null, null);

                                    if (SubtitleScaler == null)
                                    {
                                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                                        return;
                                    }

                                    if (SDL_LockTexture(sub_texture, sub_rect, pixels, &pitch) == 0)
                                    {
                                        var sourceData0 = sub_rect->data[0];
                                        var sourceStride = sub_rect->linesize[0];

                                        ffmpeg.sws_scale(SubtitleScaler, &sourceData0, &sourceStride,
                                              0, sub_rect->h, pixels, &pitch);

                                        SDL_UnlockTexture(sub_texture);
                                    }
                                }

                                sp.IsUploaded = true;
                            }
                        }
                        else
                        {
                            sp = null;
                        }
                    }
                }

                FFplay.calculate_display_rect(rect, xleft, ytop, PictureWidth, PictureHeight, vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

                if (!vp.IsUploaded)
                {
                    fixed (SwsContext** ctx = &VideoScaler)
                    {
                        if (Player.upload_texture(vp.bmp, vp.DecodedFrame, ctx) < 0)
                            return;
                    }

                    vp.IsUploaded = true;
                }

                SDL_RenderCopy(Player.renderer, vp.bmp, null, rect);

                if (sp != null)
                {
                    SDL_RenderCopy(Player.renderer, sub_texture, null, rect);
                }
            }
        }

        public void StreamTogglePause()
        {
            if (IsPaused)
            {
                frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - VideoClock.LastUpdated;
                if (ReadPauseResult != ffmpeg.AVERROR_NOTSUPP)
                {
                    VideoClock.IsPaused = false;
                }

                VideoClock.SetPosition(VideoClock.Position, VideoClock.PacketSerial);
            }

            ExternalClock.SetPosition(ExternalClock.Position, ExternalClock.PacketSerial);
            IsPaused = AudioClock.IsPaused = VideoClock.IsPaused = ExternalClock.IsPaused = !IsPaused;
        }

        public void TogglePause()
        {
            StreamTogglePause();
            IsFrameStepping = false;
        }

        public void ToggleMute()
        {
            IsAudioMuted = !IsAudioMuted;
        }

        public void UpdateVolume(int sign, int step)
        {
            AudioVolume = ffmpeg.av_clip(AudioVolume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        public void StepToNextFrame()
        {
            if (IsPaused)
                StreamTogglePause();
            IsFrameStepping = true;
        }

        public void UpdateVideoPts(double pts, long pos, int serial)
        {
            VideoClock.SetPosition(pts, serial);
            ExternalClock.SyncTo(VideoClock);
        }


        public int SynchronizeAudio(int audioSampleCount)
        {
            int wantedAudioSampleCount = audioSampleCount;

            /* if not master, then we try to remove or add samples to correct the clock */
            if (MasterSyncMode != SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                double diff, avg_diff;
                int min_nb_samples, max_nb_samples;

                diff = AudioClock.Position - MasterClockPosition;

                if (!double.IsNaN(diff) && Math.Abs(diff) < Constants.AvNoSyncThreshold)
                {
                    audio_diff_cum = diff + audio_diff_avg_coef * audio_diff_cum;
                    if (audio_diff_avg_count < Constants.AUDIO_DIFF_AVG_NB)
                    {
                        /* not enough measures to have a correct estimate */
                        audio_diff_avg_count++;
                    }
                    else
                    {
                        /* estimate the A-V difference */
                        avg_diff = audio_diff_cum * (1.0 - audio_diff_avg_coef);

                        if (Math.Abs(avg_diff) >= audio_diff_threshold)
                        {
                            wantedAudioSampleCount = audioSampleCount + (int)(diff * AudioInputParams.Frequency);
                            min_nb_samples = ((audioSampleCount * (100 - Constants.SampleCorrectionPercentMax) / 100));
                            max_nb_samples = ((audioSampleCount * (100 + Constants.SampleCorrectionPercentMax) / 100));
                            wantedAudioSampleCount = ffmpeg.av_clip(wantedAudioSampleCount, min_nb_samples, max_nb_samples);
                        }
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wantedAudioSampleCount - audioSampleCount} apts={AudioClockPosition} {audio_diff_threshold}\n");
                    }
                }
                else
                {
                    /* too big difference : may be initial PTS errors, so
                       reset A-V filter */
                    audio_diff_avg_count = 0;
                    audio_diff_cum = 0;
                }
            }

            return wantedAudioSampleCount;
        }


    }

}
