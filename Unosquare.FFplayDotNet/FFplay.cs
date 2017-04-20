namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    // https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c

    public unsafe partial class FFplay
    {
        #region Properties

        public int SoftwareScalerFlags { get; private set; } = ffmpeg.SWS_BICUBIC;
        public AVInputFormat* InputForcedFormat { get; private set; }

        public string MediaInputUrl { get; private set; }
        public string MediaTitle { get; private set; }

        internal SyncMode MediaSyncMode { get; set; } = SyncMode.AV_SYNC_AUDIO_MASTER;
        public long MediaStartTimestamp { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public long MediaDuration { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public bool? MediaSeekByBytes { get; private set; } = null;


        internal int default_width { get; set; } = 640;
        internal int default_height { get; set; } = 480;
        internal int screen_width { get; set; } = 0;
        internal int screen_height { get; set; } = 0;

        public bool IsAudioDisabled { get; private set; } = false;
        public bool IsVideoDisabled { get; private set; } = false;
        public bool IsSubtitleDisabled { get; private set; } = false;

        internal string[] wanted_stream_spec = new string[(int)AVMediaType.AVMEDIA_TYPE_NB];


        internal bool show_status { get; set; } = true;

        internal bool fast { get; set; } = false;
        internal bool genpts { get; set; } = false;
        internal bool lowres { get; set; } = false;

        internal bool autoexit { get; set; }
        internal int loop { get; set; } = 1;
        internal int framedrop { get; set; } = -1;
        internal int infinite_buffer { get; set; } = -1;

        public string AudioCodecName { get; private set; }
        public string SubtitleCodecName { get; private set; }
        public string VideoCodecName { get; private set; }

        internal bool is_full_screen { get; set; }
        internal long audio_callback_time { get; set; }

        internal SDL_Window window { get; set; }
        internal SDL_Renderer renderer { get; set; }

        internal long last_time { get; set; } = 0;

        internal readonly InterruptCallbackDelegate decode_interrupt_delegate = new InterruptCallbackDelegate(decode_interrupt_cb);
        internal readonly LockManagerCallbackDelegate lock_manager_delegate = new LockManagerCallbackDelegate(lockmgr);
        #endregion


        internal AVDictionary* FormatOptions = null;
        internal AVDictionary* CodecOptions = null;

        public FFplay()
        {
            Helper.RegisterFFmpeg();
        }

        static int lockmgr(void** mtx, AVLockOp op)
        {
            switch (op)
            {
                case AVLockOp.AV_LOCK_CREATE:
                    {
                        var mutex = SDL_CreateMutex();
                        var mutexHandle = GCHandle.Alloc(mutex, GCHandleType.Pinned);
                        *mtx = (void*)mutexHandle.AddrOfPinnedObject();

                        if (*mtx == null)
                        {
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"SDL_CreateMutex(): {SDL_GetError()}\n");
                            return 1;
                        }
                        return 0;
                    }
                case AVLockOp.AV_LOCK_OBTAIN:
                    {
                        var mutex = GCHandle.FromIntPtr(new IntPtr(*mtx)).Target as SDL_mutex;
                        SDL_LockMutex(mutex);
                        return 0;
                    }
                case AVLockOp.AV_LOCK_RELEASE:
                    {
                        var mutex = GCHandle.FromIntPtr(new IntPtr(*mtx)).Target as SDL_mutex;
                        SDL_UnlockMutex(mutex);
                        return 0;
                    }
                case AVLockOp.AV_LOCK_DESTROY:
                    {
                        var mutexHandle = GCHandle.FromIntPtr(new IntPtr(*mtx));
                        SDL_DestroyMutex(mutexHandle.Target as SDL_mutex);
                        mutexHandle.Free();

                        return 0;
                    }
            }
            return 1;
        }

        /// <summary>
        /// Port of the main method
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <param name="fromatName">Name of the fromat. Leave null for automatic selection</param>
        public void Run(string filename, string fromatName = null)
        {
            ffmpeg.avformat_network_init();
            init_opts();

            if (string.IsNullOrWhiteSpace(fromatName) == false)
            {
                InputForcedFormat = ffmpeg.av_find_input_format(fromatName);
            }

            var lockManagerCallback = new av_lockmgr_register_cb_func { Pointer = Marshal.GetFunctionPointerForDelegate(lock_manager_delegate) };
            if (ffmpeg.av_lockmgr_register(lockManagerCallback) != 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not initialize lock manager!\n");
                do_exit(null);
            }

            var vst = stream_open(filename, InputForcedFormat);

            event_loop(vst);
        }

        private int PollEvent() { return 0; }

        private EventAction refresh_loop_wait_ev(MediaState vst)
        {
            double remaining_time = 0.0;
            //SDL_PumpEvents();
            while (PollEvent() == 0)
            {

                if (remaining_time > 0.0)
                    Thread.Sleep(TimeSpan.FromSeconds(remaining_time * ffmpeg.AV_TIME_BASE));

                remaining_time = REFRESH_RATE;
                if (!vst.IsPaused || vst.IsForceRefreshRequested)
                    video_refresh(vst, ref remaining_time);

                //SDL_PumpEvents();
            }

            // TODO: still missing some code here
            return EventAction.AllocatePicture;
        }

        #region Methods

        static void free_picture(FrameHolder vp)
        {
            // TODO: free the BMP
            //if (vp->bmp)
            //{
            //    SDL_FreeYUVOverlay(vp->bmp);
            //    vp->bmp = NULL;
            //}
        }

        static int realloc_texture(SDL_Texture texture, uint new_format, int new_width, int new_height, uint blendmode, int init_texture) { return 0; }

        static void calculate_display_rect(SDL_Rect rect, int scr_xleft, int scr_ytop, int scr_width, int scr_height, int pic_width, int pic_height, AVRational pic_sar)
        {
            double aspect_ratio;
            int width, height, x, y;
            if (pic_sar.num == 0)
                aspect_ratio = 0;
            else
                aspect_ratio = Convert.ToSingle(ffmpeg.av_q2d(pic_sar));

            if (aspect_ratio <= 0.0)
                aspect_ratio = 1.0F;

            aspect_ratio *= (double)pic_width / (double)pic_height;
            height = scr_height;
            width = Convert.ToInt32(Math.Round(height * aspect_ratio)) & ~1;
            if (width > scr_width)
            {
                width = scr_width;
                height = Convert.ToInt32(Math.Round(width / aspect_ratio)) & ~1;
            }

            x = (scr_width - width) / 2;
            y = (scr_height - height) / 2;
            rect.x = scr_xleft + x;
            rect.y = scr_ytop + y;
            rect.w = Math.Max(width, 1);
            rect.h = Math.Max(height, 1);
        }

        private int upload_texture(SDL_Texture tex, AVFrame* frame, SwsContext** img_convert_ctx)
        {
            int ret = 0;

            switch ((AVPixelFormat)frame->format)
            {
                case AVPixelFormat.AV_PIX_FMT_YUV420P:
                    ret = SDL_UpdateYUVTexture(tex, null, frame->data[0], frame->linesize[0],
                                                          frame->data[1], frame->linesize[1],
                                                          frame->data[2], frame->linesize[2]);
                    break;
                case AVPixelFormat.AV_PIX_FMT_BGRA:
                    ret = SDL_UpdateTexture(tex, null, frame->data[0], frame->linesize[0]);
                    break;

                default:
                    *img_convert_ctx = ffmpeg.sws_getCachedContext(*img_convert_ctx,
                        frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height,
                        AVPixelFormat.AV_PIX_FMT_BGRA, SoftwareScalerFlags, null, null, null);
                    if (*img_convert_ctx != null)
                    {
                        byte** pixels = null;
                        int pitch = 0;
                        if (SDL_LockTexture(tex, null, pixels, &pitch) == 0)
                        {
                            var sourceData0 = frame->data[0];
                            var sourceStride = frame->linesize[0];

                            // TODO: pixels and pitch must be filled by the prior function
                            ffmpeg.sws_scale(*img_convert_ctx,
                                &sourceData0, &sourceStride, 0, frame->height,
                                pixels, &pitch);
                            SDL_UnlockTexture(tex);
                        }
                    }
                    else
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                        ret = -1;
                    }
                    break;
            }
            return ret;
        }

        private void video_image_display(MediaState vst)
        {
            var vp = new FrameHolder();
            FrameHolder sp = null;
            var rect = new SDL_Rect();

            vp = vst.VideoQueue.PeekLast();
            if (vp.bmp != null)
            {
                if (vst.SubtitleStream != null)
                {
                    if (vst.SubtitleQueue.PendingCount > 0)
                    {
                        sp = vst.SubtitleQueue.Peek();
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

                                if (realloc_texture(vst.sub_texture, SDL_PIXELFORMAT_ARGB8888, sp.PictureWidth, sp.PictureHeight, SDL_BLENDMODE_BLEND, 1) < 0)
                                    return;

                                for (var i = 0; i < sp.Subtitle.num_rects; i++)
                                {
                                    AVSubtitleRect* sub_rect = sp.Subtitle.rects[i];
                                    sub_rect->x = ffmpeg.av_clip(sub_rect->x, 0, sp.PictureWidth);
                                    sub_rect->y = ffmpeg.av_clip(sub_rect->y, 0, sp.PictureHeight);
                                    sub_rect->w = ffmpeg.av_clip(sub_rect->w, 0, sp.PictureWidth - sub_rect->x);
                                    sub_rect->h = ffmpeg.av_clip(sub_rect->h, 0, sp.PictureHeight - sub_rect->y);

                                    vst.SubtitleScaler = ffmpeg.sws_getCachedContext(vst.SubtitleScaler,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_PAL8,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_BGRA,
                                        0, null, null, null);

                                    if (vst.SubtitleScaler == null)
                                    {
                                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                                        return;
                                    }

                                    if (SDL_LockTexture(vst.sub_texture, sub_rect, pixels, &pitch) == 0)
                                    {
                                        var sourceData0 = sub_rect->data[0];
                                        var sourceStride = sub_rect->linesize[0];

                                        ffmpeg.sws_scale(vst.SubtitleScaler, &sourceData0, &sourceStride,
                                              0, sub_rect->h, pixels, &pitch);

                                        SDL_UnlockTexture(vst.sub_texture);
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

                calculate_display_rect(rect, vst.xleft, vst.ytop, vst.PictureWidth, vst.PictureHeight, vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

                if (!vp.IsUploaded)
                {
                    fixed (SwsContext** ctx = &vst.VideoScaler)
                    {
                        if (upload_texture(vp.bmp, vp.DecodedFrame, ctx) < 0)
                            return;
                    }

                    vp.IsUploaded = true;
                }

                SDL_RenderCopy(renderer, vp.bmp, null, rect);

                if (sp != null)
                {
                    SDL_RenderCopy(renderer, vst.sub_texture, null, rect);

                    int i;
                    double xratio = (double)rect.w / (double)sp.PictureWidth;
                    double yratio = (double)rect.h / (double)sp.PictureHeight;
                    for (i = 0; i < sp.Subtitle.num_rects; i++)
                    {
                        var sub_rect = sp.Subtitle.rects[i];
                        var target = new SDL_Rect
                        {
                            x = Convert.ToInt32(rect.x + sub_rect->x * xratio),
                            y = Convert.ToInt32(rect.y + sub_rect->y * yratio),
                            w = Convert.ToInt32(sub_rect->w * xratio),
                            h = Convert.ToInt32(sub_rect->h * yratio)
                        };

                        SDL_RenderCopy(renderer, vst.sub_texture, sub_rect, target);
                    }
                }
            }
        }



        private void init_opts()
        {
            var codecOpts = new AVDictionary();
            CodecOptions = &codecOpts;

            var formatOpts = new AVDictionary();
            FormatOptions = &formatOpts;
        }

        private void uninit_opts()
        {
            fixed (AVDictionary** opts_ref = &FormatOptions)
                ffmpeg.av_dict_free(opts_ref);

            fixed (AVDictionary** opts_ref = &CodecOptions)
                ffmpeg.av_dict_free(opts_ref);

        }

        static int check_stream_specifier(AVFormatContext* s, AVStream* st, string spec)
        {
            int ret = ffmpeg.avformat_match_stream_specifier(s, st, spec);
            if (ret < 0)
                ffmpeg.av_log(s, ffmpeg.AV_LOG_ERROR, $"Invalid stream specifier: {spec}.\n");
            return ret;
        }

        static AVDictionary** setup_find_stream_info_opts(AVFormatContext* s, AVDictionary* codec_opts)
        {
            var orginalOpts = new AVDictionary();
            var optsReference = &orginalOpts;
            AVDictionary** opts = &optsReference;

            if (s->nb_streams == 0) return null;

            for (var i = 0; i < s->nb_streams; i++)
            {
                opts[i] = filter_codec_opts(codec_opts, s->streams[i]->codecpar->codec_id, s, s->streams[i], null);
            }

            return opts;
        }

        // TODO: https://github.com/FFmpeg/FFmpeg/blob/d7896e9b4228e5b7ffc7ef0d0f1cf145f518c819/cmdutils.c#L2002
        static AVDictionary* filter_codec_opts(AVDictionary* opts, AVCodecID codec_id, AVFormatContext* s, AVStream* st, AVCodec* codec)
        {
            AVDictionary* ret = null;
            AVDictionaryEntry* t = null;
            var flags = s->oformat != null ? ffmpeg.AV_OPT_FLAG_ENCODING_PARAM : ffmpeg.AV_OPT_FLAG_DECODING_PARAM;
            char prefix = (char)0;
            var cc = ffmpeg.avcodec_get_class();

            if (codec == null)
                codec = (s->oformat != null) ?
                    ffmpeg.avcodec_find_encoder(codec_id) : ffmpeg.avcodec_find_decoder(codec_id);

            switch (st->codecpar->codec_type)
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
            while ((t = ffmpeg.av_dict_get(opts, "", t, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Marshal.PtrToStringAnsi(new IntPtr(t->key));
                if (string.IsNullOrWhiteSpace(key)) continue;

                var value = Marshal.PtrToStringAnsi(new IntPtr(t->value));
                var keyParts = key.Split(new char[] { ':' }, 2);

                /* check stream specification in opt name */
                if (keyParts.Length > 1)
                {
                    switch (check_stream_specifier(s, st, keyParts[1]))
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
                    ffmpeg.av_dict_set(&ret, key, value, 0);
                }
                else if (key[0] == prefix && keyParts.Length > 1 && ffmpeg.av_opt_find(&cc, keyParts[1], null, flags, ffmpeg.AV_OPT_SEARCH_FAKE_OBJ) != null)
                {
                    ffmpeg.av_dict_set(&ret, key + 1, value, 0);
                }
            }

            return ret;
        }

        private void do_exit(MediaState vst)
        {
            if (vst != null)
                vst.stream_close();

            if (renderer != null)
                SDL_DestroyRenderer(renderer);

            if (window != null)
                SDL_DestroyWindow(window);

            ffmpeg.av_lockmgr_register(null);

            uninit_opts();

            ffmpeg.avformat_network_deinit();
            if (show_status)
                Console.Write("\n");

            SDL_Quit();
            ffmpeg.av_log(null, ffmpeg.AV_LOG_QUIET, "");
            // exit
        }

        private void set_default_window_size(int width, int height, AVRational sar)
        {
            var rect = new SDL_Rect();
            calculate_display_rect(rect, 0, 0, int.MaxValue, height, width, height, sar);
            default_width = rect.w;
            default_height = rect.h;
        }

        private int video_open(MediaState vst, FrameHolder vp)
        {
            int w, h;
            if (vp != null && vp.PictureWidth != 0)
                set_default_window_size(vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

            if (screen_width != 0)
            {
                w = screen_width;
                h = screen_height;
            }
            else
            {
                w = default_width;
                h = default_height;
            }

            if (window != null)
            {
                int flags = SDL_WINDOW_SHOWN | SDL_WINDOW_RESIZABLE;

                if (is_full_screen)
                    flags |= SDL_WINDOW_FULLSCREEN_DESKTOP;

                window = SDL_CreateWindow(string.Empty, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, w, h, flags);
                SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "linear");
                if (window != null)
                {
                    var info = new SDL_RendererInfo();
                    renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
                    if (renderer != null)
                    {
                        if (SDL_GetRendererInfo(renderer, info) == 0)
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Initialized renderer.\n");
                    }
                }
            }
            else
            {
                SDL_SetWindowSize(window, w, h);
            }

            if (window == null || renderer == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "SDL: could not set video mode - exiting\n");
                do_exit(vst);
            }

            vst.PictureWidth = w;
            vst.PictureHeight = h;
            return 0;
        }

        private void video_display(MediaState vst)
        {
            if (window == null)
                video_open(vst, null);

            SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            SDL_RenderClear(renderer);

            if (vst.VideoStream != null)
                video_image_display(vst);

            SDL_RenderPresent(renderer);
        }




        static void stream_toggle_pause(MediaState vst)
        {
            if (vst.IsPaused)
            {
                vst.frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - vst.VideoClock.LastUpdated;
                if (vst.read_pause_return != AVERROR_NOTSUPP)
                {
                    vst.VideoClock.IsPaused = false;
                }
                vst.VideoClock.SetPosition(vst.VideoClock.Position, vst.VideoClock.PacketSerial);
            }
            vst.ExternalClock.SetPosition(vst.ExternalClock.Position, vst.ExternalClock.PacketSerial);
            vst.IsPaused = vst.AudioClock.IsPaused = vst.VideoClock.IsPaused = vst.ExternalClock.IsPaused = !vst.IsPaused;
        }

        static void toggle_pause(MediaState vst)
        {
            stream_toggle_pause(vst);
            vst.IsFrameStepping = false;
        }

        static void toggle_mute(MediaState vst)
        {
            vst.IsAudioMuted = !vst.IsAudioMuted;
        }

        static void update_volume(MediaState vst, int sign, int step)
        {
            vst.AudioVolume = ffmpeg.av_clip(vst.AudioVolume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        static void step_to_next_frame(MediaState vst)
        {
            if (vst.IsPaused)
                stream_toggle_pause(vst);
            vst.IsFrameStepping = true;
        }



        static void update_video_pts(MediaState vst, double pts, long pos, int serial)
        {
            vst.VideoClock.SetPosition(pts, serial);
            vst.ExternalClock.SyncTo(vst.VideoClock);
        }

        private void alloc_picture(MediaState vst)
        {
            var vp = new FrameHolder();
            uint sdl_format;
            vp = vst.VideoQueue.Frames[vst.VideoQueue.WriteIndex];
            video_open(vst, vp);
            if (vp.format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P)
                sdl_format = SDL_PIXELFORMAT_YV12;
            else
                sdl_format = SDL_PIXELFORMAT_ARGB8888;

            if (realloc_texture(vp.bmp, sdl_format, vp.PictureWidth, vp.PictureHeight, SDL_BLENDMODE_NONE, 0) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL,
                       $"Error: the video system does not support an image\n" +
                                $"size of {vp.PictureWidth}x{vp.PictureHeight} pixels. Try using -lowres or -vf \"scale=w:h\"\n" +
                                "to reduce the image size.\n");
                do_exit(vst);
            }

            SDL_LockMutex(vst.VideoQueue.mutex);
            vp.IsAllocated = true;
            SDL_CondSignal(vst.VideoQueue.cond);
            SDL_UnlockMutex(vst.VideoQueue.mutex);
        }

        private void video_refresh(MediaState vst, ref double remaining_time)
        {
            double time;
            var sp = new FrameHolder();
            var sp2 = new FrameHolder();

            if (!vst.IsPaused && vst.MasterSyncMode == SyncMode.AV_SYNC_EXTERNAL_CLOCK && vst.IsMediaRealtime)
                vst.AdjustExternalClockSpeedRatio();

            if (vst.VideoStream != null)
            {
                retry:
                if (vst.VideoQueue.PendingCount == 0)
                {
                    // nothing to do, no picture to display in the queue
                }
                else
                {
                    double last_duration, duration, delay;

                    var lastvp = vst.VideoQueue.PeekLast();
                    var vp = vst.VideoQueue.Peek();
                    if (vp.Serial != vst.VideoPackets.Serial)
                    {
                        vst.VideoQueue.frame_queue_next();
                        goto retry;
                    }
                    if (lastvp.Serial != vp.Serial)
                        vst.frame_timer = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (vst.IsPaused)
                        goto display;

                    last_duration = vst.ComputeFrameDuration(lastvp, vp);
                    delay = vst.ComputeVideoClockDelay(last_duration);
                    time = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (time < vst.frame_timer + delay)
                    {
                        remaining_time = Math.Min(vst.frame_timer + delay - time, remaining_time);
                        goto display;
                    }
                    vst.frame_timer += delay;
                    if (delay > 0 && time - vst.frame_timer > AV_SYNC_THRESHOLD_MAX)
                        vst.frame_timer = time;
                    SDL_LockMutex(vst.VideoQueue.mutex);
                    if (!double.IsNaN(vp.Pts))
                        update_video_pts(vst, vp.Pts, vp.BytePosition, vp.Serial);
                    SDL_UnlockMutex(vst.VideoQueue.mutex);
                    if (vst.VideoQueue.PendingCount > 1)
                    {
                        var nextvp = vst.VideoQueue.PeekNext();
                        duration = vst.ComputeFrameDuration(vp, nextvp);
                        if (!vst.IsFrameStepping && (framedrop > 0 || (framedrop != 0 && vst.MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER)) && time > vst.frame_timer + duration)
                        {
                            vst.frame_drops_late++;
                            vst.VideoQueue.frame_queue_next();
                            goto retry;
                        }
                    }
                    if (vst.SubtitleStream != null)
                    {
                        while (vst.SubtitleQueue.PendingCount > 0)
                        {
                            sp = vst.SubtitleQueue.Peek();
                            if (vst.SubtitleQueue.PendingCount > 1)
                                sp2 = vst.SubtitleQueue.PeekNext();
                            else
                                sp2 = null;

                            if (sp.Serial != vst.SubtitlePackets.Serial
                                    || (vst.VideoClock.Pts > (sp.Pts + ((float)sp.Subtitle.end_display_time / 1000)))
                                    || (sp2 != null && vst.VideoClock.Pts > (sp2.Pts + ((float)sp2.Subtitle.start_display_time / 1000))))
                            {
                                if (sp.IsUploaded)
                                {
                                    for (var i = 0; i < sp.Subtitle.num_rects; i++)
                                    {
                                        var sub_rect = sp.Subtitle.rects[i];
                                        byte** pixels = null;

                                        var pitch = 0;

                                        if (SDL_LockTexture(vst.sub_texture, sub_rect, pixels, &pitch) == 0)
                                        {

                                            for (var j = 0; j < sub_rect->h; j++, pixels += pitch)
                                                ffmpeg.memset(*pixels, 0, sub_rect->w << 2);

                                            SDL_UnlockTexture(vst.sub_texture);
                                        }
                                    }
                                }

                                vst.SubtitleQueue.frame_queue_next();
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    vst.VideoQueue.frame_queue_next();
                    vst.IsForceRefreshRequested = true;
                    if (vst.IsFrameStepping && !vst.IsPaused)
                        stream_toggle_pause(vst);
                }
                display:
                if (vst.IsForceRefreshRequested && vst.VideoQueue.ReadIndexShown != 0)
                    video_display(vst);
            }

            vst.IsForceRefreshRequested = false;
            if (show_status)
            {
                // static long last_time;
                long cur_time;
                int aqsize, vqsize, sqsize;
                double av_diff;
                cur_time = ffmpeg.av_gettime_relative();
                if (last_time == 0 || (cur_time - last_time) >= 30000)
                {
                    aqsize = 0;
                    vqsize = 0;
                    sqsize = 0;
                    if (vst.AudioStream != null)
                        aqsize = vst.AudioPackets.ByteLength;
                    if (vst.VideoStream != null)
                        vqsize = vst.VideoPackets.ByteLength;
                    if (vst.SubtitleStream != null)
                        sqsize = vst.SubtitlePackets.ByteLength;
                    av_diff = 0;
                    if (vst.AudioStream != null && vst.VideoStream != null)
                        av_diff = vst.AudioClock.Position - vst.VideoClock.Position;
                    else if (vst.VideoStream != null)
                        av_diff = vst.MasterClockPosition - vst.VideoClock.Position;
                    else if (vst.AudioStream != null)
                        av_diff = vst.MasterClockPosition - vst.AudioClock.Position;

                    var mode = (vst.AudioStream != null && vst.VideoStream != null) ? "A-V" : (vst.VideoStream != null ? "M-V" : (vst.AudioStream != null ? "M-A" : "   "));
                    var faultyDts = vst.VideoStream != null ? vst.VideoDecoder.Codec->pts_correction_num_faulty_dts : 0;
                    var faultyPts = vst.VideoStream != null ? vst.VideoDecoder.Codec->pts_correction_num_faulty_pts : 0;

                    ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO,
                           $"{vst.MasterClockPosition} {mode}:{av_diff} fd={vst.frame_drops_early + vst.frame_drops_late} aq={aqsize / 1024}KB vq={vqsize / 1024}KB sq={sqsize}dB f={faultyDts} / {faultyPts}\r");


                    // fflush(stdout);
                    last_time = cur_time;
                }
            }
        }


        private int get_video_frame(MediaState vst, AVFrame* frame)
        {
            int got_picture;
            if ((got_picture = vst.VideoDecoder.DecodeFrame(frame)) < 0)
                return -1;

            if (got_picture != 0)
            {
                var dpts = double.NaN;
                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    dpts = ffmpeg.av_q2d(vst.VideoStream->time_base) * frame->pts;
                frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(vst.InputContext, vst.VideoStream, frame);
                if (framedrop > 0 || (framedrop != 0 && vst.MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER))
                {
                    if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double diff = dpts - vst.MasterClockPosition;
                        if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD &&
                            diff - vst.frame_last_filter_delay < 0 &&
                            vst.VideoDecoder.PacketSerial == vst.VideoClock.PacketSerial &&
                            vst.VideoPackets.Length != 0)
                        {
                            vst.frame_drops_early++;
                            ffmpeg.av_frame_unref(frame);
                            got_picture = 0;
                        }
                    }
                }
            }
            return got_picture;
        }

        static int synchronize_audio(MediaState vst, int audioSampleCount)
        {
            int wantedAudioSampleCount = audioSampleCount;

            /* if not master, then we try to remove or add samples to correct the clock */
            if (vst.MasterSyncMode != SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                double diff, avg_diff;
                int min_nb_samples, max_nb_samples;

                diff = vst.AudioClock.Position - vst.MasterClockPosition;

                if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD)
                {
                    vst.audio_diff_cum = diff + vst.audio_diff_avg_coef * vst.audio_diff_cum;
                    if (vst.audio_diff_avg_count < AUDIO_DIFF_AVG_NB)
                    {
                        /* not enough measures to have a correct estimate */
                        vst.audio_diff_avg_count++;
                    }
                    else
                    {
                        /* estimate the A-V difference */
                        avg_diff = vst.audio_diff_cum * (1.0 - vst.audio_diff_avg_coef);

                        if (Math.Abs(avg_diff) >= vst.audio_diff_threshold)
                        {
                            wantedAudioSampleCount = audioSampleCount + (int)(diff * vst.AudioInputParams.Frequency);
                            min_nb_samples = ((audioSampleCount * (100 - SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            max_nb_samples = ((audioSampleCount * (100 + SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            wantedAudioSampleCount = ffmpeg.av_clip(wantedAudioSampleCount, min_nb_samples, max_nb_samples);
                        }
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wantedAudioSampleCount - audioSampleCount} apts={vst.AudioClockPosition} {vst.audio_diff_threshold}\n");
                    }
                }
                else
                {
                    /* too big difference : may be initial PTS errors, so
                       reset A-V filter */
                    vst.audio_diff_avg_count = 0;
                    vst.audio_diff_cum = 0;
                }
            }

            return wantedAudioSampleCount;
        }

        private int audio_decode_frame(MediaState vst)
        {
            int data_size, resampled_data_size;
            long dec_channel_layout;
            double audio_clock0;
            int wanted_nb_samples;

            if (vst.IsPaused)
                return -1;

            FrameHolder af = null;
            do
            {

                while (vst.AudioQueue.PendingCount == 0)
                {
                    if ((ffmpeg.av_gettime_relative() - audio_callback_time) > 1000000L * vst.AudioHardwareBufferSize / vst.AudioOutputParams.BytesPerSecond / 2)
                        return -1;
                    Thread.Sleep(1); //ffmpeg.av_usleep(1000);
                }

                af = vst.AudioQueue.PeekReadableFrame();
                if (af == null)
                    return -1;

                vst.AudioQueue.frame_queue_next();
            } while (af.Serial != vst.AudioPackets.Serial);

            data_size = ffmpeg.av_samples_get_buffer_size(null, ffmpeg.av_frame_get_channels(af.DecodedFrame),
                                                   af.DecodedFrame->nb_samples,
                                                   (AVSampleFormat)af.DecodedFrame->format, 1);

            dec_channel_layout =
                (af.DecodedFrame->channel_layout != 0 && ffmpeg.av_frame_get_channels(af.DecodedFrame) == ffmpeg.av_get_channel_layout_nb_channels(af.DecodedFrame->channel_layout)) ?
                    Convert.ToInt64(af.DecodedFrame->channel_layout) :
                    ffmpeg.av_get_default_channel_layout(ffmpeg.av_frame_get_channels(af.DecodedFrame));

            wanted_nb_samples = synchronize_audio(vst, af.DecodedFrame->nb_samples);

            if (af.DecodedFrame->format != (int)vst.AudioInputParams.SampleFormat ||
                dec_channel_layout != vst.AudioInputParams.ChannelLayout ||
                af.DecodedFrame->sample_rate != vst.AudioInputParams.Frequency ||
                (wanted_nb_samples != af.DecodedFrame->nb_samples && vst.AudioScaler == null))
            {
                fixed (SwrContext** vst_swr_ctx = &vst.AudioScaler)
                    ffmpeg.swr_free(vst_swr_ctx);

                vst.AudioScaler = ffmpeg.swr_alloc_set_opts(
                    null,
                    vst.AudioOutputParams.ChannelLayout, vst.AudioOutputParams.SampleFormat, vst.AudioOutputParams.Frequency,
                    dec_channel_layout, (AVSampleFormat)af.DecodedFrame->format, af.DecodedFrame->sample_rate,
                    0, null);
                if (vst.AudioScaler == null || ffmpeg.swr_init(vst.AudioScaler) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"Cannot create sample rate converter for conversion of {af.DecodedFrame->sample_rate} Hz {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)af.DecodedFrame->format)} " +
                           "{ffmpeg.av_frame_get_channels(af.frame)} channels to {vst.audio_tgt.freq} Hz {ffmpeg.av_get_sample_fmt_name(vst.audio_tgt.fmt)} {vst.audio_tgt.channels} " +
                           "channels!\n");

                    fixed (SwrContext** vst_swr_ctx = &vst.AudioScaler)
                        ffmpeg.swr_free(vst_swr_ctx);

                    return -1;
                }

                vst.AudioInputParams.ChannelLayout = dec_channel_layout;
                vst.AudioInputParams.ChannelCount = ffmpeg.av_frame_get_channels(af.DecodedFrame);
                vst.AudioInputParams.Frequency = af.DecodedFrame->sample_rate;
                vst.AudioInputParams.SampleFormat = (AVSampleFormat)af.DecodedFrame->format;
            }

            if (vst.AudioScaler != null)
            {
                var in_buffer = af.DecodedFrame->extended_data;
                var out_buffer = vst.audio_buf1;
                int out_count = wanted_nb_samples * vst.AudioOutputParams.Frequency / af.DecodedFrame->sample_rate + 256;
                int out_size = ffmpeg.av_samples_get_buffer_size(null, vst.AudioOutputParams.ChannelCount, out_count, vst.AudioOutputParams.SampleFormat, 0);
                int len2;

                if (out_size < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size() failed\n");
                    return -1;
                }

                if (wanted_nb_samples != af.DecodedFrame->nb_samples)
                {
                    if (ffmpeg.swr_set_compensation(vst.AudioScaler, (wanted_nb_samples - af.DecodedFrame->nb_samples) * vst.AudioOutputParams.Frequency / af.DecodedFrame->sample_rate,
                                                wanted_nb_samples * vst.AudioOutputParams.Frequency / af.DecodedFrame->sample_rate) < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_set_compensation() failed\n");
                        return -1;
                    }
                }
                fixed (uint* vst_audio_buf1_size = &vst.audio_buf1_size)
                    ffmpeg.av_fast_malloc((void*)vst.audio_buf1, vst_audio_buf1_size, (ulong)out_size);

                if (vst.audio_buf1 == null)
                    return ffmpeg.AVERROR_ENOMEM;

                len2 = ffmpeg.swr_convert(vst.AudioScaler, &out_buffer, out_count, in_buffer, af.DecodedFrame->nb_samples);
                if (len2 < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_convert() failed\n");
                    return -1;
                }

                if (len2 == out_count)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "audio buffer is probably too small\n");
                    if (ffmpeg.swr_init(vst.AudioScaler) < 0)
                        fixed (SwrContext** vst_swr_ctx = &vst.AudioScaler)
                            ffmpeg.swr_free(vst_swr_ctx);
                }
                vst.audio_buf = vst.audio_buf1;
                resampled_data_size = len2 * vst.AudioOutputParams.ChannelCount * ffmpeg.av_get_bytes_per_sample(vst.AudioOutputParams.SampleFormat);
            }
            else
            {
                vst.audio_buf = af.DecodedFrame->data[0];
                resampled_data_size = data_size;
            }

            audio_clock0 = vst.AudioClockPosition;
            /* update the audio clock with the pts */
            if (!double.IsNaN(af.Pts))
                vst.AudioClockPosition = af.Pts + (double)af.DecodedFrame->nb_samples / af.DecodedFrame->sample_rate;
            else
                vst.AudioClockPosition = double.NaN;

            vst.AudioClockSerial = af.Serial;

            return resampled_data_size;
        }

        private void sdl_audio_callback(MediaState vst, byte* stream, int len)
        {
            int audio_size, len1;
            audio_callback_time = ffmpeg.av_gettime_relative();
            while (len > 0)
            {
                if (vst.audio_buf_index >= vst.audio_buf_size)
                {
                    audio_size = audio_decode_frame(vst);
                    if (audio_size < 0)
                    {
                        vst.audio_buf = null;
                        vst.audio_buf_size = Convert.ToUInt32(SDL_AUDIO_MIN_BUFFER_SIZE / vst.AudioOutputParams.FrameSize * vst.AudioOutputParams.FrameSize);
                    }
                    else
                    {
                        vst.audio_buf_size = Convert.ToUInt32(audio_size);
                    }

                    vst.audio_buf_index = 0;
                }
                len1 = Convert.ToInt32(vst.audio_buf_size - vst.audio_buf_index);
                if (len1 > len) len1 = len;

                if (!vst.IsAudioMuted && vst.audio_buf != null && vst.AudioVolume == SDL_MIX_MAXVOLUME)
                {
                    ffmpeg.memcpy(stream, vst.audio_buf + vst.audio_buf_index, len1);
                }
                else
                {
                    ffmpeg.memset(stream, 0, len1);
                    if (!vst.IsAudioMuted && vst.audio_buf != null)
                        SDL_MixAudio(stream, vst.audio_buf + vst.audio_buf_index, len1, vst.AudioVolume);
                }

                len -= len1;
                stream += len1;
                vst.audio_buf_index += len1;
            }

            vst.audio_write_buf_size = Convert.ToInt32(vst.audio_buf_size - vst.audio_buf_index);
            if (!double.IsNaN(vst.AudioClockPosition))
            {
                vst.AudioClock.SetPosition(vst.AudioClockPosition - (double)(2 * vst.AudioHardwareBufferSize + vst.audio_write_buf_size) / vst.AudioOutputParams.BytesPerSecond, vst.AudioClockSerial, audio_callback_time / 1000000.0);
                vst.ExternalClock.SyncTo(vst.AudioClock);
            }
        }

        private int audio_open(MediaState vst, long wanted_channel_layout, int wanted_nb_channels, int wanted_sample_rate, AudioParams audio_hw_params)
        {
            var wanted_spec = new SDL_AudioSpec();
            var spec = new SDL_AudioSpec();

            var next_nb_channels = new int[] { 0, 0, 1, 6, 2, 6, 4, 6 };
            var next_sample_rates = new int[] { 0, 44100, 48000, 96000, 192000 };
            int next_sample_rate_idx = next_sample_rates.Length - 1;
            var env = SDL_getenv("SDL_AUDIO_CHANNELS");

            if (string.IsNullOrWhiteSpace(env) == false)
            {
                wanted_nb_channels = int.Parse(env);
                wanted_channel_layout = ffmpeg.av_get_default_channel_layout(wanted_nb_channels);
            }

            if (wanted_channel_layout == 0 || wanted_nb_channels != ffmpeg.av_get_channel_layout_nb_channels((ulong)wanted_channel_layout))
            {
                wanted_channel_layout = ffmpeg.av_get_default_channel_layout(wanted_nb_channels);
                wanted_channel_layout &= ~ffmpeg.AV_CH_LAYOUT_STEREO_DOWNMIX;
            }

            wanted_nb_channels = ffmpeg.av_get_channel_layout_nb_channels((ulong)wanted_channel_layout);
            wanted_spec.channels = wanted_nb_channels;
            wanted_spec.freq = wanted_sample_rate;
            if (wanted_spec.freq <= 0 || wanted_spec.channels <= 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Invalid sample rate or channel count!\n");
                return -1;
            }

            while (next_sample_rate_idx != 0 && next_sample_rates[next_sample_rate_idx] >= wanted_spec.freq)
                next_sample_rate_idx--;

            wanted_spec.format = AUDIO_S16SYS;
            wanted_spec.silence = 0;
            wanted_spec.samples = Math.Max(SDL_AUDIO_MIN_BUFFER_SIZE, 2 << ffmpeg.av_log2(Convert.ToUInt32(wanted_spec.freq / SDL_AUDIO_MAX_CALLBACKS_PER_SEC)));
            wanted_spec.callback = new SDL_AudioCallback(sdl_audio_callback);
            wanted_spec.userdata = vst;

            while (SDL_OpenAudio(wanted_spec, spec) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"SDL_OpenAudio ({wanted_spec.channels} channels, {wanted_spec.freq} Hz): {SDL_GetError()}\n");

                wanted_spec.channels = next_nb_channels[Math.Min(7, wanted_spec.channels)];
                if (wanted_spec.channels == 0)
                {
                    wanted_spec.freq = next_sample_rates[next_sample_rate_idx--];
                    wanted_spec.channels = wanted_nb_channels;
                    if (wanted_spec.freq == 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               "No more combinations to try, audio open failed\n");
                        return -1;
                    }
                }
                wanted_channel_layout = ffmpeg.av_get_default_channel_layout(wanted_spec.channels);
            }
            if (spec.format != AUDIO_S16SYS)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                       $"SDL advised audio format {spec.format} is not supported!\n");
                return -1;
            }
            if (spec.channels != wanted_spec.channels)
            {
                wanted_channel_layout = ffmpeg.av_get_default_channel_layout(spec.channels);
                if (wanted_channel_layout == 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"SDL advised channel count {spec.channels} is not supported!\n");
                    return -1;
                }
            }

            audio_hw_params.SampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            audio_hw_params.Frequency = spec.freq;
            audio_hw_params.ChannelLayout = wanted_channel_layout;
            audio_hw_params.ChannelCount = spec.channels;
            audio_hw_params.FrameSize = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params.ChannelCount, 1, audio_hw_params.SampleFormat, 1);
            audio_hw_params.BytesPerSecond = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params.ChannelCount, audio_hw_params.Frequency, audio_hw_params.SampleFormat, 1);

            if (audio_hw_params.BytesPerSecond <= 0 || audio_hw_params.FrameSize <= 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size failed\n");
                return -1;
            }

            return spec.size;
        }

        static bool IsFormatContextRealtime(AVFormatContext* s)
        {
            var formatName = Marshal.PtrToStringAnsi(new IntPtr(s->iformat->name));
            var filename = Encoding.GetEncoding(0).GetString(s->filename);
            if (formatName.Equals("rtp")
               || formatName.Equals("rtsp")
               || formatName.Equals("sdp")
            )
                return true;

            if (s->pb != null &&
                (filename.StartsWith("rtp:") || filename.StartsWith("udp:")))
                return true;

            return false;
        }

        static bool HasEnoughPackets(AVStream* stream, int streamIndex, PacketQueue queue)
        {
            return
                (streamIndex < 0) ||
                (queue.IsAborted) ||
                ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) ||
                queue.Length > MIN_FRAMES && (queue.Duration == 0 || ffmpeg.av_q2d(stream->time_base) * queue.Duration > 1.0);
        }

        static int decode_interrupt_cb(void* opaque)
        {
            var vst = GCHandle.FromIntPtr(new IntPtr(opaque)).Target as MediaState;
            return vst.IsAbortRequested ? 1 : 0;
        }

        private int decoder_start(Decoder d, Func<MediaState, int> fn, MediaState vst)
        {
            d.DecoderThread = SDL_CreateThread(fn, vst);
            if (d.DecoderThread == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"SDL_CreateThread(): {SDL_GetError()}\n");
                return ffmpeg.AVERROR_ENOMEM;
            }

            return 0;
        }

        private int video_thread(MediaState vst)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            double pts;
            double duration;
            int ret;
            var tb = vst.VideoStream->time_base;
            var frame_rate = ffmpeg.av_guess_frame_rate(vst.InputContext, vst.VideoStream, null);

            while (true)
            {
                ret = get_video_frame(vst, frame);
                if (ret < 0)
                    break;

                if (ret == 0)
                    continue;

                duration = (frame_rate.num != 0 && frame_rate.den != 0 ? ffmpeg.av_q2d(new AVRational { num = frame_rate.den, den = frame_rate.num }) : 0);
                pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
                ret = vst.EnqueuePicture(frame, pts, duration);
                ffmpeg.av_frame_unref(frame);

                if (ret < 0)
                    break;
            }

            ffmpeg.av_frame_free(&frame);
            return 0;
        }

        private int subtitle_thread(MediaState vst)
        {
            FrameHolder sp = null;
            int got_subtitle;
            double pts;

            while (true)
            {
                sp = vst.SubtitleQueue.PeekWritableFrame();
                if (sp == null) return 0;

                fixed (AVSubtitle* sp_sub = &sp.Subtitle)
                    got_subtitle = vst.SubtitleDecoder.DecodeFrame(sp_sub);

                if (got_subtitle < 0) break;

                pts = 0;

                if (got_subtitle != 0 && sp.Subtitle.format == 0)
                {
                    if (sp.Subtitle.pts != ffmpeg.AV_NOPTS_VALUE)
                        pts = sp.Subtitle.pts / (double)ffmpeg.AV_TIME_BASE;

                    sp.Pts = pts;
                    sp.Serial = vst.SubtitleDecoder.PacketSerial;
                    sp.PictureWidth = vst.SubtitleDecoder.Codec->width;
                    sp.PictureHeight = vst.SubtitleDecoder.Codec->height;
                    sp.IsUploaded = false;

                    /* now we can update the picture count */
                    vst.SubtitleQueue.frame_queue_push();
                }
                else if (got_subtitle != 0)
                {
                    fixed (AVSubtitle* sp_sub = &sp.Subtitle)
                        ffmpeg.avsubtitle_free(sp_sub);
                }
            }

            return 0;
        }

        private int audio_thread(MediaState vst)
        {
            var decodedFrame = ffmpeg.av_frame_alloc();
            int gotFrame = 0;
            int ret = 0;

            do
            {
                gotFrame = vst.AudioDecoder.DecodeFrame(decodedFrame);

                if (gotFrame < 0) break;

                if (gotFrame != 0)
                {
                    var timeBase = new AVRational { num = 1, den = decodedFrame->sample_rate };
                    var audioFrame = vst.AudioQueue.PeekWritableFrame();
                    if (audioFrame == null) break;

                    audioFrame.Pts = (decodedFrame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : decodedFrame->pts * ffmpeg.av_q2d(timeBase);
                    audioFrame.BytePosition = ffmpeg.av_frame_get_pkt_pos(decodedFrame);
                    audioFrame.Serial = vst.AudioDecoder.PacketSerial;
                    audioFrame.EstimatedDuration = ffmpeg.av_q2d(new AVRational { num = decodedFrame->nb_samples, den = decodedFrame->sample_rate });

                    ffmpeg.av_frame_move_ref(audioFrame.DecodedFrame, decodedFrame);
                    vst.AudioQueue.frame_queue_push();

                }
            } while (ret >= 0 || ret == ffmpeg.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF);

            ffmpeg.av_frame_free(&decodedFrame);
            return ret;
        }

        private int read_thread(MediaState vst)
        {
            AVFormatContext* ic = null;
            int err, i, ret;
            var st_index = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            AVPacket pkt1;
            AVPacket* pkt = &pkt1;
            long stream_start_time;
            bool pkt_in_play_range = false;
            AVDictionaryEntry* t;
            AVDictionary** opts;
            int orig_nb_streams;
            SDL_mutex wait_mutex = SDL_CreateMutex();
            var scan_all_pmts_set = false;
            long pkt_ts;
            if (wait_mutex == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"SDL_CreateMutex(): {SDL_GetError()}\n");
                ret = ffmpeg.AVERROR_ENOMEM;
                goto fail;
            }

            ffmpeg.memset(st_index, -1, st_index.Length);
            vst.last_video_stream = vst.VideoStreamIndex = -1;
            vst.last_audio_stream = vst.AudioStreamIndex = -1;
            vst.last_subtitle_stream = vst.SubtitleStreamIndex = -1;
            vst.IsAtEndOfFile = false;
            ic = ffmpeg.avformat_alloc_context();
            if (ic == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not allocate context.\n");
                ret = ffmpeg.AVERROR_ENOMEM;
                goto fail;
            }

            ic->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(decode_interrupt_delegate) };
            ic->interrupt_callback.opaque = (void*)vst.Handle.AddrOfPinnedObject();

            fixed (AVDictionary** format_opts_ref = &FormatOptions)
            {
                if (ffmpeg.av_dict_get(FormatOptions, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE) == null)
                {
                    ffmpeg.av_dict_set(format_opts_ref, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
                    scan_all_pmts_set = true;
                }

                err = ffmpeg.avformat_open_input(&ic, vst.MediaUrl, vst.InputFormat, format_opts_ref);
                if (err < 0)
                {
                    Debug.WriteLine($"Error in read_thread. File '{vst.MediaUrl}'. {err}");
                    ret = -1;
                    goto fail;
                }

                if (scan_all_pmts_set)
                    ffmpeg.av_dict_set(format_opts_ref, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE);

                t = ffmpeg.av_dict_get(FormatOptions, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX);

            }

            if (t != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Marshal.PtrToStringAnsi(new IntPtr(t->key))} not found.\n");
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            vst.InputContext = ic;
            if (genpts)
                ic->flags |= ffmpeg.AVFMT_FLAG_GENPTS;

            ffmpeg.av_format_inject_global_side_data(ic);

            opts = setup_find_stream_info_opts(ic, CodecOptions);
            orig_nb_streams = Convert.ToInt32(ic->nb_streams);
            err = ffmpeg.avformat_find_stream_info(ic, opts);
            for (i = 0; i < orig_nb_streams; i++)
                ffmpeg.av_dict_free(&opts[i]);

            ffmpeg.av_freep(&opts);

            if (err < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{vst.MediaUrl}: could not find codec parameters\n");
                ret = -1;
                goto fail;
            }

            if (ic->pb != null)
                ic->pb->eof_reached = 0; // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end

            var formatName = Marshal.PtrToStringAnsi(new IntPtr(ic->iformat->name));
            var isDiscontinuous = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) == 0;

            // seek by byes only for continuous ogg vorbis
            if (MediaSeekByBytes.HasValue == false)
                MediaSeekByBytes = !isDiscontinuous && formatName.Equals("ogg");

            vst.MaximumFrameDuration = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0 ? 10.0 : 3600.0;

            t = ffmpeg.av_dict_get(ic->metadata, "title", null, 0);
            if (t != null)
                MediaTitle = Marshal.PtrToStringAnsi(new IntPtr(t->value));


            if (MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE)
            {
                long timestamp;
                timestamp = MediaStartTimestamp;
                if (ic->start_time != ffmpeg.AV_NOPTS_VALUE)
                    timestamp += ic->start_time;

                ret = ffmpeg.avformat_seek_file(ic, -1, long.MinValue, timestamp, long.MaxValue, 0);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{vst.MediaUrl}: could not seek to position {((double)timestamp / ffmpeg.AV_TIME_BASE)}\n");
                }
            }

            vst.IsMediaRealtime = IsFormatContextRealtime(ic);
            if (show_status)
                ffmpeg.av_dump_format(ic, 0, vst.MediaUrl, 0);

            for (i = 0; i < ic->nb_streams; i++)
            {
                var st = ic->streams[i];
                var type = st->codecpar->codec_type;
                st->discard = AVDiscard.AVDISCARD_ALL;
                if (type >= 0 && string.IsNullOrWhiteSpace(wanted_stream_spec[(int)type]) == false && st_index[(int)type] == -1)
                    if (ffmpeg.avformat_match_stream_specifier(ic, st, wanted_stream_spec[(int)type]) > 0)
                        st_index[(int)type] = i;
            }

            for (i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
            {
                if (string.IsNullOrWhiteSpace(wanted_stream_spec[i]) == false && st_index[i] == -1)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                        $"Stream specifier {wanted_stream_spec[i]} does not match any {ffmpeg.av_get_media_type_string((AVMediaType)i)} stream\n");

                    st_index[i] = int.MaxValue;
                }
            }

            if (!IsVideoDisabled)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);

            if (!IsAudioDisabled)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        null, 0);

            if (!IsVideoDisabled && !IsSubtitleDisabled)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                        (st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                         st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                        null, 0);

            //vst.show_mode = show_mode;
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                var st = ic->streams[st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]];
                var codecpar = st->codecpar;
                var sar = ffmpeg.av_guess_sample_aspect_ratio(ic, st, null);
                if (codecpar->width != 0)
                    set_default_window_size(codecpar->width, codecpar->height, sar);
            }
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
            {
                vst.stream_component_open(st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO], this);
            }

            ret = -1;
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                ret = vst.stream_component_open(st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], this);
            }

            //if (vst.show_mode == ShowMode.SHOW_MODE_NONE)
            //    vst.show_mode = ret >= 0 ? ShowMode.SHOW_MODE_VIDEO : ShowMode.SHOW_MODE_NONE;

            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
            {
                vst.stream_component_open(st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE], this);
            }

            if (vst.VideoStreamIndex < 0 && vst.AudioStreamIndex < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Failed to open file '{vst.MediaUrl}' or configure filtergraph\n");
                ret = -1;
                goto fail;
            }

            if (infinite_buffer < 0 && vst.IsMediaRealtime)
                infinite_buffer = 1;

            while (true)
            {
                if (vst.IsAbortRequested)
                    break;

                if (Convert.ToInt32(vst.IsPaused) != vst.last_paused)
                {
                    vst.last_paused = Convert.ToInt32(vst.IsPaused);

                    if (vst.IsPaused)
                        vst.read_pause_return = ffmpeg.av_read_pause(ic);
                    else
                        ffmpeg.av_read_play(ic);
                }

                if (vst.IsPaused &&
                        (formatName.Equals("rtsp") ||
                         (ic->pb != null && MediaInputUrl.StartsWith("mmsh:"))))
                {
                    SDL_Delay(10);
                    continue;
                }

                if (vst.IsSeekRequested)
                {
                    long seek_target = vst.seek_pos;
                    long seek_min = vst.seek_rel > 0 ? seek_target - vst.seek_rel + 2 : long.MinValue;
                    long seek_max = vst.seek_rel < 0 ? seek_target - vst.seek_rel - 2 : long.MaxValue;
                    // FIXME the +-2 is due to rounding being not done in the correct direction in generation
                    //      of the seek_pos/seek_rel variables
                    ret = ffmpeg.avformat_seek_file(vst.InputContext, -1, seek_min, seek_target, seek_max, vst.seek_flags);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               $"{Encoding.GetEncoding(0).GetString(vst.InputContext->filename)}: error while seeking\n");
                    }
                    else
                    {
                        if (vst.AudioStreamIndex >= 0)
                        {
                            vst.AudioPackets.Clear();
                            vst.AudioPackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if (vst.SubtitleStreamIndex >= 0)
                        {
                            vst.SubtitlePackets.Clear();
                            vst.SubtitlePackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if (vst.VideoStreamIndex >= 0)
                        {
                            vst.VideoPackets.Clear();
                            vst.VideoPackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if ((vst.seek_flags & ffmpeg.AVSEEK_FLAG_BYTE) != 0)
                        {
                            vst.ExternalClock.SetPosition(double.NaN, 0);
                        }
                        else
                        {
                            vst.ExternalClock.SetPosition(seek_target / (double)ffmpeg.AV_TIME_BASE, 0);
                        }
                    }

                    vst.IsSeekRequested = false;
                    vst.queue_attachments_req = true;
                    vst.IsAtEndOfFile = false;
                    if (vst.IsPaused)
                        step_to_next_frame(vst);
                }

                if (vst.queue_attachments_req)
                {
                    if (vst.VideoStream != null && (vst.VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        var copy = new AVPacket();
                        if ((ret = ffmpeg.av_copy_packet(&copy, &vst.VideoStream->attached_pic)) < 0)
                            goto fail;

                        vst.VideoPackets.Enqueue(&copy);
                        vst.VideoPackets.EnqueueNull(vst.VideoStreamIndex);
                    }

                    vst.queue_attachments_req = false;
                }

                if (infinite_buffer < 1 &&
                      (vst.AudioPackets.ByteLength + vst.VideoPackets.ByteLength + vst.SubtitlePackets.ByteLength > MAX_QUEUE_SIZE
                    || (HasEnoughPackets(vst.AudioStream, vst.AudioStreamIndex, vst.AudioPackets) &&
                        HasEnoughPackets(vst.VideoStream, vst.VideoStreamIndex, vst.VideoPackets) &&
                        HasEnoughPackets(vst.SubtitleStream, vst.SubtitleStreamIndex, vst.SubtitlePackets))))
                {
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vst.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }

                if (!vst.IsPaused &&
                    (vst.AudioStream == null || (vst.AudioDecoder.IsFinished == Convert.ToBoolean(vst.AudioPackets.Serial) && vst.AudioQueue.PendingCount == 0)) &&
                    (vst.VideoStream == null || (vst.VideoDecoder.IsFinished == Convert.ToBoolean(vst.VideoPackets.Serial) && vst.VideoQueue.PendingCount == 0)))
                {
                    if (loop != 1 && (loop == 0 || --loop == 0))
                    {
                        vst.SeekTo(MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0, 0, false);
                    }
                    else if (autoexit)
                    {
                        ret = ffmpeg.AVERROR_EOF;
                        goto fail;
                    }
                }
                ret = ffmpeg.av_read_frame(ic, pkt);
                if (ret < 0)
                {
                    if ((ret == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) && !vst.IsAtEndOfFile)
                    {
                        if (vst.VideoStreamIndex >= 0)
                            vst.VideoPackets.EnqueueNull(vst.VideoStreamIndex);
                        if (vst.AudioStreamIndex >= 0)
                            vst.AudioPackets.EnqueueNull(vst.AudioStreamIndex);
                        if (vst.SubtitleStreamIndex >= 0)
                            vst.SubtitlePackets.EnqueueNull(vst.SubtitleStreamIndex);

                        vst.IsAtEndOfFile = true;
                    }

                    if (ic->pb != null && ic->pb->error != 0)
                        break;

                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vst.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                else
                {
                    vst.IsAtEndOfFile = false;
                }

                stream_start_time = ic->streams[pkt->stream_index]->start_time;
                pkt_ts = pkt->pts == ffmpeg.AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                pkt_in_play_range = MediaDuration == ffmpeg.AV_NOPTS_VALUE ||
                        (pkt_ts - (stream_start_time != ffmpeg.AV_NOPTS_VALUE ? stream_start_time : 0)) *
                        ffmpeg.av_q2d(ic->streams[pkt->stream_index]->time_base) -
                        (double)(MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0) / 1000000
                        <= ((double)MediaDuration / 1000000);

                if (pkt->stream_index == vst.AudioStreamIndex && pkt_in_play_range)
                {
                    vst.AudioPackets.Enqueue(pkt);
                }
                else if (pkt->stream_index == vst.VideoStreamIndex && pkt_in_play_range
                         && (vst.VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    vst.VideoPackets.Enqueue(pkt);
                }
                else if (pkt->stream_index == vst.SubtitleStreamIndex && pkt_in_play_range)
                {
                    vst.SubtitlePackets.Enqueue(pkt);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }

            ret = 0;
            fail:
            if (ic != null && vst.InputContext == null)
                ffmpeg.avformat_close_input(&ic);

            if (ret != 0)
            {
                var ev = new SDL_Event();
                ev.type = FF_QUIT_EVENT;
                ev.user_data1 = vst;
                SDL_PushEvent(ev);
            }

            SDL_DestroyMutex(wait_mutex);
            return 0;
        }

        private MediaState stream_open(string filename, AVInputFormat* iformat)
        {
            var vst = new MediaState();

            vst.MediaUrl = filename;
            vst.InputFormat = iformat;
            vst.ytop = 0;
            vst.xleft = 0;

            vst.VideoQueue = new FrameQueue(vst.VideoPackets, VIDEO_PICTURE_QUEUE_SIZE, true);
            vst.SubtitleQueue = new FrameQueue(vst.SubtitlePackets, SUBPICTURE_QUEUE_SIZE, false);
            vst.AudioQueue = new FrameQueue(vst.AudioPackets, SAMPLE_QUEUE_SIZE, true);

            vst.continue_read_thread = SDL_CreateCond();

            vst.VideoClock = new Clock(() => { return new int?(vst.VideoPackets.Serial); });
            vst.AudioClock = new Clock(() => { return new int?(vst.AudioPackets.Serial); });
            vst.ExternalClock = new Clock(() => { return new int?(vst.ExternalClock.PacketSerial); });

            vst.AudioClockSerial = -1;
            vst.AudioVolume = SDL_MIX_MAXVOLUME;
            vst.IsAudioMuted = false;
            vst.MediaSyncMode = MediaSyncMode;
            vst.ReadThread = SDL_CreateThread(read_thread, vst);

            return vst;

            // fail:
            //stream_close(vst);

            //return vst;
        }


        private void event_loop(MediaState mediaState)
        {
            double incr;
            double pos;

            while (true)
            {
                EventAction action = refresh_loop_wait_ev(mediaState);
                switch (action)
                {
                    case EventAction.Quit:
                        do_exit(mediaState);
                        break;
                    case EventAction.ToggleFullScreen:
                        //toggle_full_screen(cur_stream);
                        mediaState.IsForceRefreshRequested = true;
                        break;
                    case EventAction.TogglePause:
                        toggle_pause(mediaState);
                        break;
                    case EventAction.ToggleMute:
                        toggle_mute(mediaState);
                        break;
                    case EventAction.VolumeUp:
                        update_volume(mediaState, 1, SDL_VOLUME_STEP);
                        break;
                    case EventAction.VolumeDown:
                        update_volume(mediaState, -1, SDL_VOLUME_STEP);
                        break;
                    case EventAction.StepNextFrame:
                        step_to_next_frame(mediaState);
                        break;
                    case EventAction.CycleAudio:
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_AUDIO, this);
                        break;
                    case EventAction.CycleVideo:
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_VIDEO, this);
                        break;
                    case EventAction.CycleAll:
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_VIDEO, this);
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_AUDIO, this);
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_SUBTITLE, this);
                        break;
                    case EventAction.CycleSubtitles:
                        mediaState.stream_cycle_channel(AVMediaType.AVMEDIA_TYPE_SUBTITLE, this);
                        break;
                    case EventAction.NextChapter:
                        if (mediaState.InputContext->nb_chapters <= 1)
                        {
                            incr = 600.0;
                            goto do_seek;
                        }
                        mediaState.SeekChapter(1);
                        break;
                    case EventAction.PreviousChapter:
                        if (mediaState.InputContext->nb_chapters <= 1)
                        {
                            incr = -600.0;
                            goto do_seek;
                        }
                        mediaState.SeekChapter(-1);
                        break;
                    case EventAction.SeekLeft10:
                        incr = -10.0;
                        goto do_seek;
                    case EventAction.SeekRight10:
                        incr = 10.0;
                        goto do_seek;
                    case EventAction.SeekLRight60:
                        incr = 60.0;
                        goto do_seek;
                    case EventAction.SeekLeft60:
                        incr = -60.0;
                        do_seek:
                        if (MediaSeekByBytes.HasValue && MediaSeekByBytes.Value == true)
                        {
                            pos = -1;
                            if (pos < 0 && mediaState.VideoStreamIndex >= 0)
                                pos = mediaState.VideoQueue.StreamPosition;

                            if (pos < 0 && mediaState.AudioStreamIndex >= 0)
                                pos = mediaState.AudioQueue.StreamPosition;

                            if (pos < 0)
                                pos = mediaState.InputContext->pb->pos; // TODO: ffmpeg.avio_tell(cur_stream.ic->pb); avio_tell not available here

                            if (mediaState.InputContext->bit_rate != 0)
                                incr *= mediaState.InputContext->bit_rate / 8.0;
                            else
                                incr *= 180000.0;

                            pos += incr;
                            mediaState.SeekTo((long)pos, (long)incr, true);
                        }
                        else
                        {
                            pos = mediaState.MasterClockPosition;
                            if (double.IsNaN(pos))
                                pos = (double)mediaState.seek_pos / ffmpeg.AV_TIME_BASE;

                            pos += incr;

                            if (mediaState.InputContext->start_time != ffmpeg.AV_NOPTS_VALUE && pos < mediaState.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE)
                                pos = mediaState.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE;

                            mediaState.SeekTo((long)(pos * ffmpeg.AV_TIME_BASE), (long)(incr * ffmpeg.AV_TIME_BASE), false);
                        }
                        break;
                    case EventAction.AllocatePicture:
                        alloc_picture(mediaState);
                        break;
                    default:
                        break;
                }
            }
        }

        #endregion

    }
}