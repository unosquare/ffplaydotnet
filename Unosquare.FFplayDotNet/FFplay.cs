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


        public bool? IsPtsReorderingEnabled { get; private set; } = null;

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

        static void free_picture(Frame vp)
        {
            // TODO: free the BMP
            //if (vp->bmp)
            //{
            //    SDL_FreeYUVOverlay(vp->bmp);
            //    vp->bmp = NULL;
            //}
        }
        static void InitializeDecoder(Decoder d, AVCodecContext* avctx, PacketQueue queue, SDL_cond empty_queue_cond)
        {
            d.avctx = avctx;
            d.queue = queue;
            d.empty_queue_cond = empty_queue_cond;
            d.start_pts = ffmpeg.AV_NOPTS_VALUE;
        }

        private int DecodeFrame(Decoder d, AVFrame* frame, AVSubtitle* sub)
        {
            int got_frame = 0;
            do
            {
                int ret = -1;
                if (d.queue.IsPendingAbort)
                    return -1;
                if (!d.IsPacketPending || d.queue.Serial != d.pkt_serial)
                {
                    var pkt = new AVPacket();
                    do
                    {
                        if (d.queue.Length == 0)
                            SDL_CondSignal(d.empty_queue_cond);
                        if (d.queue.Dequeue(&pkt, true, ref d.pkt_serial) < 0)
                            return -1;
                        if (pkt.data == PacketQueue.FlushPacket->data)
                        {
                            ffmpeg.avcodec_flush_buffers(d.avctx);
                            d.finished = false;
                            d.next_pts = d.start_pts;
                            d.next_pts_tb = d.start_pts_tb;
                        }
                    } while (pkt.data == PacketQueue.FlushPacket->data || d.queue.Serial != d.pkt_serial);
                    fixed (AVPacket* refPacket = &d.pkt)
                    {
                        ffmpeg.av_packet_unref(refPacket);
                    }

                    d.pkt_temp = d.pkt = pkt;
                    d.IsPacketPending = true;
                }
                switch (d.avctx->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        fixed (AVPacket* pktTemp = &d.pkt_temp)
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            ret = ffmpeg.avcodec_decode_video2(d.avctx, frame, &got_frame, pktTemp);
#pragma warning restore CS0618 // Type or member is obsolete
                        }
                        if (got_frame != 0)
                        {
                            if (IsPtsReorderingEnabled.HasValue == false)
                            {
                                frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
                            }
                            else if (IsPtsReorderingEnabled.HasValue && IsPtsReorderingEnabled.Value == false)
                            {
                                frame->pts = frame->pkt_dts;
                            }
                        }
                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        fixed (AVPacket* pktTemp = &d.pkt_temp)
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            ret = ffmpeg.avcodec_decode_audio4(d.avctx, frame, &got_frame, pktTemp);
#pragma warning restore CS0618 // Type or member is obsolete
                        }
                        if (got_frame != 0)
                        {
                            var tb = new AVRational { num = 1, den = frame->sample_rate };
                            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(frame->pts, ffmpeg.av_codec_get_pkt_timebase(d.avctx), tb);
                            else if (d.next_pts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(d.next_pts, d.next_pts_tb, tb);
                            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                            {
                                d.next_pts = frame->pts + frame->nb_samples;
                                d.next_pts_tb = tb;
                            }
                        }
                        break;
                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        fixed (AVPacket* pktTemp = &d.pkt_temp)
                        {
                            ret = ffmpeg.avcodec_decode_subtitle2(d.avctx, sub, &got_frame, pktTemp);
                        }
                        break;
                }
                if (ret < 0)
                {
                    d.IsPacketPending = false;
                }
                else
                {
                    d.pkt_temp.dts =
                    d.pkt_temp.pts = ffmpeg.AV_NOPTS_VALUE;
                    if (d.pkt_temp.data != null)
                    {
                        if (d.avctx->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                            ret = d.pkt_temp.size;

                        d.pkt_temp.data += ret;
                        d.pkt_temp.size -= ret;
                        if (d.pkt_temp.size <= 0)
                            d.IsPacketPending = false;
                    }
                    else
                    {
                        if (got_frame == 0)
                        {
                            d.IsPacketPending = false;
                            d.finished = Convert.ToBoolean(d.pkt_serial);
                        }
                    }
                }
            } while (!Convert.ToBoolean(got_frame) && !d.finished);
            return got_frame;
        }

        static void DecoderDestroy(Decoder d)
        {
            fixed (AVPacket* packetRef = &d.pkt)
            {
                ffmpeg.av_packet_unref(packetRef);
            }

            fixed (AVCodecContext** refContext = &d.avctx)
            {
                ffmpeg.avcodec_free_context(refContext);
            }
        }

        static void frame_queue_unref_item(Frame vp)
        {
            ffmpeg.av_frame_unref(vp.DecodedFrame);
            fixed (AVSubtitle* vpsub = &vp.Subtitle)
            {
                ffmpeg.avsubtitle_free(vpsub);
            }


        }

        static int frame_queue_init(FrameQueue f, PacketQueue pktq, int max_size, bool keep_last)
        {
            f.mutex = SDL_CreateMutex();
            f.cond = SDL_CreateCond();

            f.pktq = pktq;
            f.max_size = Math.Min(max_size, FRAME_QUEUE_SIZE);
            f.keep_last = !!keep_last;
            for (var i = 0; i < f.max_size; i++)
                f.queue[i].DecodedFrame = ffmpeg.av_frame_alloc();

            return 0;
        }

        static void frame_queue_destory(FrameQueue f)
        {
            for (var i = 0; i < f.max_size; i++)
            {
                Frame vp = f.queue[i];
                frame_queue_unref_item(vp);
                fixed (AVFrame** frameRef = &vp.DecodedFrame)
                {
                    ffmpeg.av_frame_free(frameRef);
                }

                free_picture(vp);
            }
            SDL_DestroyMutex(f.mutex);
            SDL_DestroyCond(f.cond);
        }

        static void frame_queue_signal(FrameQueue f)
        {
            SDL_LockMutex(f.mutex);
            SDL_CondSignal(f.cond);
            SDL_UnlockMutex(f.mutex);
        }

        static Frame frame_queue_peek(FrameQueue f)
        {
            return f.queue[(f.rindex + f.rindex_shown) % f.max_size];
        }

        static Frame frame_queue_peek_next(FrameQueue f)
        {
            return f.queue[(f.rindex + f.rindex_shown + 1) % f.max_size];
        }

        static Frame frame_queue_peek_last(FrameQueue f)
        {
            return f.queue[f.rindex];
        }

        static Frame frame_queue_peek_writable(FrameQueue f)
        {
            SDL_LockMutex(f.mutex);
            while (f.size >= f.max_size &&
                   !f.pktq.IsPendingAbort)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);
            if (f.pktq.IsPendingAbort)
                return null;
            return f.queue[f.windex];
        }

        static Frame frame_queue_peek_readable(FrameQueue f)
        {
            SDL_LockMutex(f.mutex);
            while (f.size - f.rindex_shown <= 0 &&
                   !f.pktq.IsPendingAbort)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);
            if (f.pktq.IsPendingAbort)
                return null;
            return f.queue[(f.rindex + f.rindex_shown) % f.max_size];
        }

        static void frame_queue_push(FrameQueue f)
        {
            if (++f.windex == f.max_size)
                f.windex = 0;
            SDL_LockMutex(f.mutex);
            f.size++;
            SDL_CondSignal(f.cond);
            SDL_UnlockMutex(f.mutex);
        }

        static void frame_queue_next(FrameQueue f)
        {
            if (f.keep_last && !Convert.ToBoolean(f.rindex_shown))
            {
                f.rindex_shown = 1;
                return;
            }
            frame_queue_unref_item(f.queue[f.rindex]);
            if (++f.rindex == f.max_size)
                f.rindex = 0;
            SDL_LockMutex(f.mutex);
            f.size--;
            SDL_CondSignal(f.cond);
            SDL_UnlockMutex(f.mutex);
        }

        static int frame_queue_nb_remaining(FrameQueue f)
        {
            return f.size - f.rindex_shown;
        }

        static long frame_queue_last_pos(FrameQueue f)
        {
            var fp = f.queue[f.rindex];
            if (f.rindex_shown != 0 && fp.Serial == f.pktq.Serial)
                return fp.BytePosition;
            else
                return -1;
        }

        static void decoder_abort(Decoder d, FrameQueue fq)
        {
            d.queue.Lock();
            frame_queue_signal(fq);
            SDL_WaitThread(d.DecoderThread, null);
            d.DecoderThread = null;
            d.queue.Flush();
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
            var vp = new Frame();
            Frame sp = null;
            var rect = new SDL_Rect();

            vp = frame_queue_peek_last(vst.PictureQueue);
            if (vp.bmp != null)
            {
                if (vst.subtitle_st != null)
                {
                    if (frame_queue_nb_remaining(vst.SubtitleQueue) > 0)
                    {
                        sp = frame_queue_peek(vst.SubtitleQueue);
                        if (vp.PresentationTimestamp >= sp.PresentationTimestamp + ((float)sp.Subtitle.start_display_time / 1000))
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

                                    vst.sub_convert_ctx = ffmpeg.sws_getCachedContext(vst.sub_convert_ctx,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_PAL8,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_BGRA,
                                        0, null, null, null);

                                    if (vst.sub_convert_ctx == null)
                                    {
                                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                                        return;
                                    }

                                    if (SDL_LockTexture(vst.sub_texture, sub_rect, pixels, &pitch) == 0)
                                    {
                                        var sourceData0 = sub_rect->data[0];
                                        var sourceStride = sub_rect->linesize[0];

                                        ffmpeg.sws_scale(vst.sub_convert_ctx, &sourceData0, &sourceStride,
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

                calculate_display_rect(rect, vst.xleft, vst.ytop, vst.PictureWidth, vst.height, vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

                if (!vp.IsUploaded)
                {
                    fixed (SwsContext** ctx = &vst.img_convert_ctx)
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

        private static void stream_component_close(MediaState vst, int stream_index)
        {
            AVFormatContext* ic = vst.InputContext;
            AVCodecParameters* codecpar;
            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return;
            codecpar = ic->streams[stream_index]->codecpar;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    decoder_abort(vst.AudioDecoder, vst.AudioQueue);
                    SDL_CloseAudio();
                    DecoderDestroy(vst.AudioDecoder);
                    fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                    {
                        ffmpeg.swr_free(vst_swr_ctx);
                    }

                    ffmpeg.av_freep((void*)vst.audio_buf1);
                    vst.audio_buf1_size = 0;
                    vst.audio_buf = null;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    decoder_abort(vst.VideoDecoder, vst.PictureQueue);
                    DecoderDestroy(vst.VideoDecoder);
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    decoder_abort(vst.SubtitleDecoder, vst.SubtitleQueue);
                    DecoderDestroy(vst.SubtitleDecoder);
                    break;
                default:
                    break;
            }
            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_ALL;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    vst.AudioStream = null;
                    vst.audio_stream = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vst.video_st = null;
                    vst.VideoStreamIndex = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vst.subtitle_st = null;
                    vst.SubtitleStreamIndex = -1;
                    break;
                default:
                    break;
            }
        }

        static void stream_close(MediaState vst)
        {
            vst.IsAbortRequested = true;
            SDL_WaitThread(vst.ReadThread, null);
            if (vst.audio_stream >= 0)
                stream_component_close(vst, vst.audio_stream);
            if (vst.VideoStreamIndex >= 0)
                stream_component_close(vst, vst.VideoStreamIndex);
            if (vst.SubtitleStreamIndex >= 0)
                stream_component_close(vst, vst.SubtitleStreamIndex);
            fixed (AVFormatContext** vstic = &vst.InputContext)
            {
                ffmpeg.avformat_close_input(vstic);
            }

            vst.VideoPackets.Destroy();
            vst.AudioPackets.Destroy();
            vst.SubtitlePackets.Destroy();
            frame_queue_destory(vst.PictureQueue);
            frame_queue_destory(vst.AudioQueue);
            frame_queue_destory(vst.SubtitleQueue);
            SDL_DestroyCond(vst.continue_read_thread);
            ffmpeg.sws_freeContext(vst.img_convert_ctx);
            ffmpeg.sws_freeContext(vst.sub_convert_ctx);

            if (vst.vis_texture != null)
                SDL_DestroyTexture(vst.vis_texture);
            if (vst.sub_texture != null)
                SDL_DestroyTexture(vst.sub_texture);
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
                stream_close(vst);

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

        private int video_open(MediaState vst, Frame vp)
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
            vst.height = h;
            return 0;
        }

        private void video_display(MediaState vst)
        {
            if (window == null)
                video_open(vst, null);
            SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            SDL_RenderClear(renderer);

            if (vst.video_st != null)
                video_image_display(vst);

            SDL_RenderPresent(renderer);
        }

        static double get_clock(Clock c)
        {
            if (c.queue_serial.HasValue == false || c.queue_serial.Value != c.serial)
                return double.NaN;

            if (c.paused)
            {
                return c.pts;
            }
            else
            {
                double time = ffmpeg.av_gettime_relative() / 1000000.0;
                return c.pts_drift + time - (time - c.last_updated) * (1.0 - c.speed);
            }
        }

        static void set_clock_at(Clock c, double pts, int serial, double time)
        {
            c.pts = pts;
            c.last_updated = time;
            c.pts_drift = c.pts - time;
            c.serial = serial;
        }

        static void set_clock(Clock c, double pts, int serial)
        {
            double time = ffmpeg.av_gettime_relative() / 1000000.0;
            set_clock_at(c, pts, serial, time);
        }

        static void set_clock_speed(Clock c, double speed)
        {
            set_clock(c, get_clock(c), c.serial);
            c.speed = speed;
        }

        static void init_clock(Clock c, int? queue_serial)
        {
            c.speed = 1.0;
            c.paused = false;
            c.queue_serial = queue_serial;
            set_clock(c, double.NaN, -1);
        }

        static void sync_clock_to_slave(Clock c, Clock slave)
        {
            double clock = get_clock(c);
            double slave_clock = get_clock(slave);
            if (!double.IsNaN(slave_clock) && (double.IsNaN(clock) || Math.Abs(clock - slave_clock) > AV_NOSYNC_THRESHOLD))
                set_clock(c, slave_clock, slave.serial);
        }

        static double get_master_clock(MediaState vst)
        {
            double val;
            switch (vst.MasterSyncMode)
            {
                case SyncMode.AV_SYNC_VIDEO_MASTER:
                    val = get_clock(vst.VideoClock);
                    break;
                case SyncMode.AV_SYNC_AUDIO_MASTER:
                    val = get_clock(vst.AudioClock);
                    break;
                default:
                    val = get_clock(vst.ExternalClock);
                    break;
            }
            return val;
        }

        static void check_external_clock_speed(MediaState vst)
        {
            if (vst.VideoStreamIndex >= 0 && vst.VideoPackets.Length <= EXTERNAL_CLOCK_MIN_FRAMES ||
                vst.audio_stream >= 0 && vst.AudioPackets.Length <= EXTERNAL_CLOCK_MIN_FRAMES)
            {
                set_clock_speed(vst.ExternalClock, Math.Max(EXTERNAL_CLOCK_SPEED_MIN, vst.ExternalClock.speed - EXTERNAL_CLOCK_SPEED_STEP));
            }
            else if ((vst.VideoStreamIndex < 0 || vst.VideoPackets.Length > EXTERNAL_CLOCK_MAX_FRAMES) &&
                     (vst.audio_stream < 0 || vst.AudioPackets.Length > EXTERNAL_CLOCK_MAX_FRAMES))
            {
                set_clock_speed(vst.ExternalClock, Math.Min(EXTERNAL_CLOCK_SPEED_MAX, vst.ExternalClock.speed + EXTERNAL_CLOCK_SPEED_STEP));
            }
            else
            {
                double speed = vst.ExternalClock.speed;
                if (speed != 1.0)
                    set_clock_speed(vst.ExternalClock, speed + EXTERNAL_CLOCK_SPEED_STEP * (1.0 - speed) / Math.Abs(1.0 - speed));
            }
        }

        static void stream_seek(MediaState vst, long pos, long rel, bool seek_by_bytes)
        {
            if (!vst.IsSeekRequested)
            {
                vst.seek_pos = pos;
                vst.seek_rel = rel;
                vst.seek_flags &= ~ffmpeg.AVSEEK_FLAG_BYTE;
                if (seek_by_bytes) vst.seek_flags |= ffmpeg.AVSEEK_FLAG_BYTE;
                vst.IsSeekRequested = true;
                SDL_CondSignal(vst.continue_read_thread);
            }
        }

        static void stream_toggle_pause(MediaState vst)
        {
            if (vst.IsPaused)
            {
                vst.frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - vst.VideoClock.last_updated;
                if (vst.read_pause_return != AVERROR_NOTSUPP)
                {
                    vst.VideoClock.paused = false;
                }
                set_clock(vst.VideoClock, get_clock(vst.VideoClock), vst.VideoClock.serial);
            }
            set_clock(vst.ExternalClock, get_clock(vst.ExternalClock), vst.ExternalClock.serial);
            vst.IsPaused = vst.AudioClock.paused = vst.VideoClock.paused = vst.ExternalClock.paused = !vst.IsPaused;
        }

        static void toggle_pause(MediaState vst)
        {
            stream_toggle_pause(vst);
            vst.step = false;
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
            vst.step = true;
        }

        static double compute_target_delay(double delay, MediaState vst)
        {
            double sync_threshold, diff = 0;
            if (vst.MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                diff = get_clock(vst.VideoClock) - get_master_clock(vst);
                sync_threshold = Math.Max(AV_SYNC_THRESHOLD_MIN, Math.Min(AV_SYNC_THRESHOLD_MAX, delay));
                if (!double.IsNaN(diff) && Math.Abs(diff) < vst.MaximumFrameDuration)
                {
                    if (diff <= -sync_threshold)
                        delay = Math.Max(0, delay + diff);
                    else if (diff >= sync_threshold && delay > AV_SYNC_FRAMEDUP_THRESHOLD)
                        delay = delay + diff;
                    else if (diff >= sync_threshold)
                        delay = 2 * delay;
                }
            }
            ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, "video: delay={delay} A-V={-diff}\n");
            return delay;
        }

        static double vp_duration(MediaState vst, Frame vp, Frame nextvp)
        {
            if (vp.Serial == nextvp.Serial)
            {
                double duration = nextvp.PresentationTimestamp - vp.PresentationTimestamp;
                if (double.IsNaN(duration) || duration <= 0 || duration > vst.MaximumFrameDuration)
                    return vp.EstimatedDuration;
                else
                    return duration;
            }
            else
            {
                return 0.0;
            }
        }

        static void update_video_pts(MediaState vst, double pts, long pos, int serial)
        {
            set_clock(vst.VideoClock, pts, serial);
            sync_clock_to_slave(vst.ExternalClock, vst.VideoClock);
        }

        private void alloc_picture(MediaState vst)
        {
            var vp = new Frame();
            uint sdl_format;
            vp = vst.PictureQueue.queue[vst.PictureQueue.windex];
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

            SDL_LockMutex(vst.PictureQueue.mutex);
            vp.IsAllocated = true;
            SDL_CondSignal(vst.PictureQueue.cond);
            SDL_UnlockMutex(vst.PictureQueue.mutex);
        }

        private void video_refresh(MediaState vst, ref double remaining_time)
        {
            double time;
            var sp = new Frame();
            var sp2 = new Frame();

            if (!vst.IsPaused && vst.MasterSyncMode == SyncMode.AV_SYNC_EXTERNAL_CLOCK && vst.IsMediaRealtime)
                check_external_clock_speed(vst);

            if (vst.video_st != null)
            {
                retry:
                if (frame_queue_nb_remaining(vst.PictureQueue) == 0)
                {
                    // nothing to do, no picture to display in the queue
                }
                else
                {
                    double last_duration, duration, delay;

                    var lastvp = frame_queue_peek_last(vst.PictureQueue);
                    var vp = frame_queue_peek(vst.PictureQueue);
                    if (vp.Serial != vst.VideoPackets.Serial)
                    {
                        frame_queue_next(vst.PictureQueue);
                        goto retry;
                    }
                    if (lastvp.Serial != vp.Serial)
                        vst.frame_timer = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (vst.IsPaused)
                        goto display;

                    last_duration = vp_duration(vst, lastvp, vp);
                    delay = compute_target_delay(last_duration, vst);
                    time = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (time < vst.frame_timer + delay)
                    {
                        remaining_time = Math.Min(vst.frame_timer + delay - time, remaining_time);
                        goto display;
                    }
                    vst.frame_timer += delay;
                    if (delay > 0 && time - vst.frame_timer > AV_SYNC_THRESHOLD_MAX)
                        vst.frame_timer = time;
                    SDL_LockMutex(vst.PictureQueue.mutex);
                    if (!double.IsNaN(vp.PresentationTimestamp))
                        update_video_pts(vst, vp.PresentationTimestamp, vp.BytePosition, vp.Serial);
                    SDL_UnlockMutex(vst.PictureQueue.mutex);
                    if (frame_queue_nb_remaining(vst.PictureQueue) > 1)
                    {
                        var nextvp = frame_queue_peek_next(vst.PictureQueue);
                        duration = vp_duration(vst, vp, nextvp);
                        if (!vst.step && (framedrop > 0 || (framedrop != 0 && vst.MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER)) && time > vst.frame_timer + duration)
                        {
                            vst.frame_drops_late++;
                            frame_queue_next(vst.PictureQueue);
                            goto retry;
                        }
                    }
                    if (vst.subtitle_st != null)
                    {
                        while (frame_queue_nb_remaining(vst.SubtitleQueue) > 0)
                        {
                            sp = frame_queue_peek(vst.SubtitleQueue);
                            if (frame_queue_nb_remaining(vst.SubtitleQueue) > 1)
                                sp2 = frame_queue_peek_next(vst.SubtitleQueue);
                            else
                                sp2 = null;

                            if (sp.Serial != vst.SubtitlePackets.Serial
                                    || (vst.VideoClock.pts > (sp.PresentationTimestamp + ((float)sp.Subtitle.end_display_time / 1000)))
                                    || (sp2 != null && vst.VideoClock.pts > (sp2.PresentationTimestamp + ((float)sp2.Subtitle.start_display_time / 1000))))
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
                                frame_queue_next(vst.SubtitleQueue);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    frame_queue_next(vst.PictureQueue);
                    vst.IsForceRefreshRequested = true;
                    if (vst.step && !vst.IsPaused)
                        stream_toggle_pause(vst);
                }
                display:
                if (vst.IsForceRefreshRequested && vst.PictureQueue.rindex_shown != 0)
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
                    if (vst.video_st != null)
                        vqsize = vst.VideoPackets.ByteLength;
                    if (vst.subtitle_st != null)
                        sqsize = vst.SubtitlePackets.ByteLength;
                    av_diff = 0;
                    if (vst.AudioStream != null && vst.video_st != null)
                        av_diff = get_clock(vst.AudioClock) - get_clock(vst.VideoClock);
                    else if (vst.video_st != null)
                        av_diff = get_master_clock(vst) - get_clock(vst.VideoClock);
                    else if (vst.AudioStream != null)
                        av_diff = get_master_clock(vst) - get_clock(vst.AudioClock);

                    var mode = (vst.AudioStream != null && vst.video_st != null) ? "A-V" : (vst.video_st != null ? "M-V" : (vst.AudioStream != null ? "M-A" : "   "));
                    var faultyDts = vst.video_st != null ? vst.VideoDecoder.avctx->pts_correction_num_faulty_dts : 0;
                    var faultyPts = vst.video_st != null ? vst.VideoDecoder.avctx->pts_correction_num_faulty_pts : 0;

                    ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO,
                           $"{get_master_clock(vst)} {mode}:{av_diff} fd={vst.frame_drops_early + vst.frame_drops_late} aq={aqsize / 1024}KB vq={vqsize / 1024}KB sq={sqsize}dB f={faultyDts} / {faultyPts}\r");


                    // fflush(stdout);
                    last_time = cur_time;
                }
            }
        }

        static int queue_picture(MediaState vst, AVFrame* src_frame, double pts, double duration, long pos, int serial)
        {
            var vp = frame_queue_peek_writable(vst.PictureQueue);

            Debug.WriteLine($"frame_type={ffmpeg.av_get_picture_type_char(src_frame->pict_type)} pts={pts}");

            if (vp == null)
                return -1;

            vp.PictureAspectRatio = src_frame->sample_aspect_ratio;
            vp.IsUploaded = false;

            if (vp.bmp == null || !vp.IsAllocated ||
                vp.PictureWidth != src_frame->width ||
                vp.PictureHeight != src_frame->height ||
                vp.format != src_frame->format)
            {
                var ev = new SDL_Event();
                vp.IsAllocated = false;
                vp.PictureWidth = src_frame->width;
                vp.PictureHeight = src_frame->height;
                vp.format = src_frame->format;
                ev.type = FF_ALLOC_EVENT;
                ev.user_data1 = vst;
                SDL_PushEvent(ev);
                SDL_LockMutex(vst.PictureQueue.mutex);

                while (!vp.IsAllocated && !vst.VideoPackets.IsPendingAbort)
                {
                    SDL_CondWait(vst.PictureQueue.cond, vst.PictureQueue.mutex);
                }
                if (vst.VideoPackets.IsPendingAbort && SDL_PeepEvents(ev, 1, SDL_GETEVENT, FF_ALLOC_EVENT, FF_ALLOC_EVENT) != 1)
                {
                    while (!vp.IsAllocated && !vst.IsAbortRequested)
                    {
                        SDL_CondWait(vst.PictureQueue.cond, vst.PictureQueue.mutex);
                    }
                }
                SDL_UnlockMutex(vst.PictureQueue.mutex);
                if (vst.VideoPackets.IsPendingAbort)
                    return -1;
            }
            if (vp.bmp != null)
            {
                vp.PresentationTimestamp = pts;
                vp.EstimatedDuration = duration;
                vp.BytePosition = pos;
                vp.Serial = serial;
                ffmpeg.av_frame_move_ref(vp.DecodedFrame, src_frame);
                frame_queue_push(vst.PictureQueue);
            }
            return 0;
        }

        private int get_video_frame(MediaState vst, AVFrame* frame)
        {
            int got_picture;
            if ((got_picture = DecodeFrame(vst.VideoDecoder, frame, null)) < 0)
                return -1;

            if (got_picture != 0)
            {
                var dpts = double.NaN;
                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    dpts = ffmpeg.av_q2d(vst.video_st->time_base) * frame->pts;
                frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(vst.InputContext, vst.video_st, frame);
                if (framedrop > 0 || (framedrop != 0 && vst.MasterSyncMode != SyncMode.AV_SYNC_VIDEO_MASTER))
                {
                    if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double diff = dpts - get_master_clock(vst);
                        if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD &&
                            diff - vst.frame_last_filter_delay < 0 &&
                            vst.VideoDecoder.pkt_serial == vst.VideoClock.serial &&
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

                diff = get_clock(vst.AudioClock) - get_master_clock(vst);

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
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wantedAudioSampleCount - audioSampleCount} apts={vst.audio_clock} {vst.audio_diff_threshold}\n");
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

            Frame af = null;
            do
            {

                while (frame_queue_nb_remaining(vst.AudioQueue) == 0)
                {
                    if ((ffmpeg.av_gettime_relative() - audio_callback_time) > 1000000L * vst.audio_hw_buf_size / vst.AudioOutputParams.BytesPerSecond / 2)
                        return -1;
                    Thread.Sleep(1); //ffmpeg.av_usleep(1000);
                }

                af = frame_queue_peek_readable(vst.AudioQueue);
                if (af == null)
                    return -1;

                frame_queue_next(vst.AudioQueue);
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
                (wanted_nb_samples != af.DecodedFrame->nb_samples && vst.swr_ctx == null))
            {
                fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                    ffmpeg.swr_free(vst_swr_ctx);

                vst.swr_ctx = ffmpeg.swr_alloc_set_opts(
                    null,
                    vst.AudioOutputParams.ChannelLayout, vst.AudioOutputParams.SampleFormat, vst.AudioOutputParams.Frequency,
                    dec_channel_layout, (AVSampleFormat)af.DecodedFrame->format, af.DecodedFrame->sample_rate,
                    0, null);
                if (vst.swr_ctx == null || ffmpeg.swr_init(vst.swr_ctx) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"Cannot create sample rate converter for conversion of {af.DecodedFrame->sample_rate} Hz {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)af.DecodedFrame->format)} " +
                           "{ffmpeg.av_frame_get_channels(af.frame)} channels to {vst.audio_tgt.freq} Hz {ffmpeg.av_get_sample_fmt_name(vst.audio_tgt.fmt)} {vst.audio_tgt.channels} " +
                           "channels!\n");

                    fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                        ffmpeg.swr_free(vst_swr_ctx);

                    return -1;
                }

                vst.AudioInputParams.ChannelLayout = dec_channel_layout;
                vst.AudioInputParams.ChannelCount = ffmpeg.av_frame_get_channels(af.DecodedFrame);
                vst.AudioInputParams.Frequency = af.DecodedFrame->sample_rate;
                vst.AudioInputParams.SampleFormat = (AVSampleFormat)af.DecodedFrame->format;
            }

            if (vst.swr_ctx != null)
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
                    if (ffmpeg.swr_set_compensation(vst.swr_ctx, (wanted_nb_samples - af.DecodedFrame->nb_samples) * vst.AudioOutputParams.Frequency / af.DecodedFrame->sample_rate,
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

                len2 = ffmpeg.swr_convert(vst.swr_ctx, &out_buffer, out_count, in_buffer, af.DecodedFrame->nb_samples);
                if (len2 < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_convert() failed\n");
                    return -1;
                }

                if (len2 == out_count)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "audio buffer is probably too small\n");
                    if (ffmpeg.swr_init(vst.swr_ctx) < 0)
                        fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                            ffmpeg.swr_free(vst_swr_ctx);
                }
                vst.audio_buf = vst.audio_buf1;
                resampled_data_size = len2 * vst.AudioOutputParams.ChannelCount * ffmpeg.av_get_bytes_per_sample(vst.AudioOutputParams.SampleFormat);
            }
            else
            {
                var x = af.DecodedFrame->data[0];

                vst.audio_buf = af.DecodedFrame->data[0];
                resampled_data_size = data_size;
            }

            audio_clock0 = vst.audio_clock;
            /* update the audio clock with the pts */
            if (!double.IsNaN(af.PresentationTimestamp))
                vst.audio_clock = af.PresentationTimestamp + (double)af.DecodedFrame->nb_samples / af.DecodedFrame->sample_rate;
            else
                vst.audio_clock = double.NaN;

            vst.audio_clock_serial = af.Serial;

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
            if (!double.IsNaN(vst.audio_clock))
            {
                set_clock_at(vst.AudioClock, vst.audio_clock - (double)(2 * vst.audio_hw_buf_size + vst.audio_write_buf_size) / vst.AudioOutputParams.BytesPerSecond, vst.audio_clock_serial, audio_callback_time / 1000000.0);
                sync_clock_to_slave(vst.ExternalClock, vst.AudioClock);
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
                (queue.IsPendingAbort) ||
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
            d.queue.Unlock();
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
            var tb = vst.video_st->time_base;
            var frame_rate = ffmpeg.av_guess_frame_rate(vst.InputContext, vst.video_st, null);

            while (true)
            {
                ret = get_video_frame(vst, frame);
                if (ret < 0)
                    break;

                if (ret == 0)
                    continue;

                duration = (frame_rate.num != 0 && frame_rate.den != 0 ? ffmpeg.av_q2d(new AVRational { num = frame_rate.den, den = frame_rate.num }) : 0);
                pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
                ret = queue_picture(vst, frame, pts, duration, ffmpeg.av_frame_get_pkt_pos(frame), vst.VideoDecoder.pkt_serial);
                ffmpeg.av_frame_unref(frame);


                if (ret < 0)
                    break;
            }

            ffmpeg.av_frame_free(&frame);
            return 0;
        }

        private int subtitle_thread(MediaState vst)
        {
            Frame sp = null;
            int got_subtitle;
            double pts;

            while (true)
            {
                sp = frame_queue_peek_writable(vst.SubtitleQueue);
                if (sp == null) return 0;

                fixed (AVSubtitle* sp_sub = &sp.Subtitle)
                    got_subtitle = DecodeFrame(vst.SubtitleDecoder, null, sp_sub);

                if (got_subtitle < 0) break;

                pts = 0;

                if (got_subtitle != 0 && sp.Subtitle.format == 0)
                {
                    if (sp.Subtitle.pts != ffmpeg.AV_NOPTS_VALUE)
                        pts = sp.Subtitle.pts / (double)ffmpeg.AV_TIME_BASE;

                    sp.PresentationTimestamp = pts;
                    sp.Serial = vst.SubtitleDecoder.pkt_serial;
                    sp.PictureWidth = vst.SubtitleDecoder.avctx->width;
                    sp.PictureHeight = vst.SubtitleDecoder.avctx->height;
                    sp.IsUploaded = false;

                    /* now we can update the picture count */
                    frame_queue_push(vst.SubtitleQueue);
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
            var frame = ffmpeg.av_frame_alloc();
            Frame af;
            int got_frame = 0;
            AVRational tb;
            int ret = 0;

            do
            {
                got_frame = DecodeFrame(vst.AudioDecoder, frame, null);

                if (got_frame < 0) break;

                if (got_frame != 0)
                {
                    tb = new AVRational { num = 1, den = frame->sample_rate };
                    af = frame_queue_peek_writable(vst.AudioQueue);
                    if (af == null) break;

                    af.PresentationTimestamp = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
                    af.BytePosition = ffmpeg.av_frame_get_pkt_pos(frame);
                    af.Serial = vst.AudioDecoder.pkt_serial;
                    af.EstimatedDuration = ffmpeg.av_q2d(new AVRational { num = frame->nb_samples, den = frame->sample_rate });

                    ffmpeg.av_frame_move_ref(af.DecodedFrame, frame);
                    frame_queue_push(vst.AudioQueue);

                }
            } while (ret >= 0 || ret == ffmpeg.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF);

            ffmpeg.av_frame_free(&frame);
            return ret;
        }

        private int stream_component_open(MediaState vst, int stream_index)
        {
            var ic = vst.InputContext;
            string forced_codec_name = null;
            AVDictionaryEntry* t = null;

            int sample_rate = 0;
            int nb_channels = 0;
            long channel_layout = 0;
            int ret = 0;
            int stream_lowres = Convert.ToInt32(lowres);
            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return -1;

            var avctx = ffmpeg.avcodec_alloc_context3(null);
            if (avctx == null)
                return ffmpeg.AVERROR_ENOMEM;

            ret = ffmpeg.avcodec_parameters_to_context(avctx, ic->streams[stream_index]->codecpar);
            if (ret < 0) goto fail;
            ffmpeg.av_codec_set_pkt_timebase(avctx, ic->streams[stream_index]->time_base);
            var codec = ffmpeg.avcodec_find_decoder(avctx->codec_id);
            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO: vst.last_audio_stream = stream_index; forced_codec_name = AudioCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: vst.last_subtitle_stream = stream_index; forced_codec_name = SubtitleCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO: vst.last_video_stream = stream_index; forced_codec_name = VideoCodecName; break;
            }
            if (string.IsNullOrWhiteSpace(forced_codec_name) == false)
                codec = ffmpeg.avcodec_find_decoder_by_name(forced_codec_name);

            if (codec == null)
            {
                if (string.IsNullOrWhiteSpace(forced_codec_name) == false)
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with name '{forced_codec_name}'\n");
                else
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with id {avctx->codec_id}\n");

                ret = ffmpeg.AVERROR_EINVAL;
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

            if (fast)
                avctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            var opts = filter_codec_opts(CodecOptions, avctx->codec_id, ic, ic->streams[stream_index], codec);

            if (ffmpeg.av_dict_get(opts, "threads", null, 0) == null)
                ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            if (stream_lowres != 0)
                ffmpeg.av_dict_set_int(&opts, "lowres", stream_lowres, 0);

            if (avctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || avctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                ffmpeg.av_dict_set(&opts, "refcounted_frames", "1", 0);

            if ((ret = ffmpeg.avcodec_open2(avctx, codec, &opts)) < 0)
            {
                goto fail;
            }

            if ((t = ffmpeg.av_dict_get(opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Marshal.PtrToStringAnsi(new IntPtr(t->key))} not found.\n");
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            vst.IsAtEndOfFile = false;
            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_DEFAULT;

            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    if ((ret = audio_open(vst, channel_layout, nb_channels, sample_rate, vst.AudioOutputParams)) < 0)
                        goto fail;

                    vst.audio_hw_buf_size = ret;
                    vst.AudioOutputParams.CopyTo(vst.AudioInputParams);
                    vst.audio_buf_size = 0;
                    vst.audio_buf_index = 0;
                    vst.audio_diff_avg_coef = Math.Exp(Math.Log(0.01) / AUDIO_DIFF_AVG_NB);
                    vst.audio_diff_avg_count = 0;
                    vst.audio_diff_threshold = (double)(vst.audio_hw_buf_size) / vst.AudioOutputParams.BytesPerSecond;
                    vst.audio_stream = stream_index;
                    vst.AudioStream = ic->streams[stream_index];

                    InitializeDecoder(vst.AudioDecoder, avctx, vst.AudioPackets, vst.continue_read_thread);

                    if ((vst.InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                        vst.InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
                    {
                        vst.AudioDecoder.start_pts = vst.AudioStream->start_time;
                        vst.AudioDecoder.start_pts_tb = vst.AudioStream->time_base;
                    }

                    if ((ret = decoder_start(vst.AudioDecoder, audio_thread, vst)) < 0)
                        goto final;

                    SDL_PauseAudio(0);
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vst.VideoStreamIndex = stream_index;
                    vst.video_st = ic->streams[stream_index];
                    InitializeDecoder(vst.VideoDecoder, avctx, vst.VideoPackets, vst.continue_read_thread);
                    if ((ret = decoder_start(vst.VideoDecoder, video_thread, vst)) < 0)
                        goto final;
                    vst.queue_attachments_req = true;
                    break;

                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vst.SubtitleStreamIndex = stream_index;
                    vst.subtitle_st = ic->streams[stream_index];
                    InitializeDecoder(vst.SubtitleDecoder, avctx, vst.SubtitlePackets, vst.continue_read_thread);
                    if ((ret = decoder_start(vst.SubtitleDecoder, subtitle_thread, vst)) < 0)
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
            vst.last_audio_stream = vst.audio_stream = -1;
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
                stream_component_open(vst, st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]);
            }

            ret = -1;
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                ret = stream_component_open(vst, st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]);
            }

            //if (vst.show_mode == ShowMode.SHOW_MODE_NONE)
            //    vst.show_mode = ret >= 0 ? ShowMode.SHOW_MODE_VIDEO : ShowMode.SHOW_MODE_NONE;

            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
            {
                stream_component_open(vst, st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE]);
            }

            if (vst.VideoStreamIndex < 0 && vst.audio_stream < 0)
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
                        if (vst.audio_stream >= 0)
                        {
                            vst.AudioPackets.Flush();
                            vst.AudioPackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if (vst.SubtitleStreamIndex >= 0)
                        {
                            vst.SubtitlePackets.Flush();
                            vst.SubtitlePackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if (vst.VideoStreamIndex >= 0)
                        {
                            vst.VideoPackets.Flush();
                            vst.VideoPackets.Enqueue(PacketQueue.FlushPacket);
                        }
                        if ((vst.seek_flags & ffmpeg.AVSEEK_FLAG_BYTE) != 0)
                        {
                            set_clock(vst.ExternalClock, double.NaN, 0);
                        }
                        else
                        {
                            set_clock(vst.ExternalClock, seek_target / (double)ffmpeg.AV_TIME_BASE, 0);
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
                    if (vst.video_st != null && (vst.video_st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        var copy = new AVPacket();
                        if ((ret = ffmpeg.av_copy_packet(&copy, &vst.video_st->attached_pic)) < 0)
                            goto fail;

                        vst.VideoPackets.Enqueue(&copy);
                        vst.VideoPackets.EnqueueNull(vst.VideoStreamIndex);
                    }

                    vst.queue_attachments_req = false;
                }
                if (infinite_buffer < 1 &&
                      (vst.AudioPackets.ByteLength + vst.VideoPackets.ByteLength + vst.SubtitlePackets.ByteLength > MAX_QUEUE_SIZE
                    || (HasEnoughPackets(vst.AudioStream, vst.audio_stream, vst.AudioPackets) &&
                        HasEnoughPackets(vst.video_st, vst.VideoStreamIndex, vst.VideoPackets) &&
                        HasEnoughPackets(vst.subtitle_st, vst.SubtitleStreamIndex, vst.SubtitlePackets))))
                {
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vst.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                if (!vst.IsPaused &&
                    (vst.AudioStream == null || (vst.AudioDecoder.finished == Convert.ToBoolean(vst.AudioPackets.Serial) && frame_queue_nb_remaining(vst.AudioQueue) == 0)) &&
                    (vst.video_st == null || (vst.VideoDecoder.finished == Convert.ToBoolean(vst.VideoPackets.Serial) && frame_queue_nb_remaining(vst.PictureQueue) == 0)))
                {
                    if (loop != 1 && (loop == 0 || --loop == 0))
                    {
                        stream_seek(vst, MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0, 0, false);
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
                        if (vst.audio_stream >= 0)
                            vst.AudioPackets.EnqueueNull(vst.audio_stream);
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

                if (pkt->stream_index == vst.audio_stream && pkt_in_play_range)
                {
                    vst.AudioPackets.Enqueue(pkt);
                }
                else if (pkt->stream_index == vst.VideoStreamIndex && pkt_in_play_range
                         && (vst.video_st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
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
            if (frame_queue_init(vst.PictureQueue, vst.VideoPackets, VIDEO_PICTURE_QUEUE_SIZE, true) < 0)
                goto fail;
            if (frame_queue_init(vst.SubtitleQueue, vst.SubtitlePackets, SUBPICTURE_QUEUE_SIZE, false) < 0)
                goto fail;
            if (frame_queue_init(vst.AudioQueue, vst.AudioPackets, SAMPLE_QUEUE_SIZE, true) < 0)
                goto fail;

            vst.continue_read_thread = SDL_CreateCond();

            init_clock(vst.VideoClock, vst.VideoPackets.Serial);
            init_clock(vst.AudioClock, vst.AudioPackets.Serial);
            init_clock(vst.ExternalClock, vst.ExternalClock.serial);
            vst.audio_clock_serial = -1;
            vst.AudioVolume = SDL_MIX_MAXVOLUME;
            vst.IsAudioMuted = false;
            vst.MediaSyncMode = MediaSyncMode;
            vst.ReadThread = SDL_CreateThread(read_thread, vst);

            fail:
            stream_close(vst);

            return vst;
        }

        static void seek_chapter(MediaState vst, int incr)
        {
            long pos = Convert.ToInt64(get_master_clock(vst) * ffmpeg.AV_TIME_BASE);
            int i = 0;

            if (vst.InputContext->nb_chapters == 0)
                return;

            for (i = 0; i < vst.InputContext->nb_chapters; i++)
            {
                AVChapter* ch = vst.InputContext->chapters[i];
                if (ffmpeg.av_compare_ts(pos, ffmpeg.AV_TIME_BASE_Q, ch->start, ch->time_base) < 0)
                {
                    i--;
                    break;
                }
            }

            i += incr;
            i = Math.Max(i, 0);
            if (i >= vst.InputContext->nb_chapters)
                return;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Seeking to chapter {i}.\n");
            stream_seek(vst, ffmpeg.av_rescale_q(vst.InputContext->chapters[i]->start, vst.InputContext->chapters[i]->time_base,
                                         ffmpeg.AV_TIME_BASE_Q), 0, false);
        }

        private void stream_cycle_channel(MediaState vst, AVMediaType codec_type)
        {
            var ic = vst.InputContext;
            int start_index, stream_index;
            int old_index;
            AVStream* st;
            AVProgram* p = null;
            int nb_streams = (int)vst.InputContext->nb_streams;

            if (codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                start_index = vst.last_video_stream;
                old_index = vst.VideoStreamIndex;
            }
            else if (codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                start_index = vst.last_audio_stream;
                old_index = vst.audio_stream;
            }
            else
            {
                start_index = vst.last_subtitle_stream;
                old_index = vst.SubtitleStreamIndex;
            }
            stream_index = start_index;
            if (codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO && vst.VideoStreamIndex != -1)
            {
                p = ffmpeg.av_find_program_from_stream(ic, null, vst.VideoStreamIndex);
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
                        vst.last_subtitle_stream = -1;
                        goto the_end;
                    }
                    if (start_index == -1)
                        return;
                    stream_index = 0;
                }
                if (stream_index == start_index)
                    return;
                st = vst.InputContext->streams[p != null ? (int)p->stream_index[stream_index] : stream_index];
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
            stream_component_close(vst, old_index);
            stream_component_open(vst, stream_index);
        }


        private void event_loop(MediaState cur_stream)
        {
            double incr;
            double pos;

            while (true)
            {
                EventAction action = refresh_loop_wait_ev(cur_stream);
                switch (action)
                {
                    case EventAction.Quit:
                        do_exit(cur_stream);
                        break;
                    case EventAction.ToggleFullScreen:
                        //toggle_full_screen(cur_stream);
                        cur_stream.IsForceRefreshRequested = true;
                        break;
                    case EventAction.TogglePause:
                        toggle_pause(cur_stream);
                        break;
                    case EventAction.ToggleMute:
                        toggle_mute(cur_stream);
                        break;
                    case EventAction.VolumeUp:
                        update_volume(cur_stream, 1, SDL_VOLUME_STEP);
                        break;
                    case EventAction.VolumeDown:
                        update_volume(cur_stream, -1, SDL_VOLUME_STEP);
                        break;
                    case EventAction.StepNextFrame:
                        step_to_next_frame(cur_stream);
                        break;
                    case EventAction.CycleAudio:
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_AUDIO);
                        break;
                    case EventAction.CycleVideo:
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_VIDEO);
                        break;
                    case EventAction.CycleAll:
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_VIDEO);
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_AUDIO);
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case EventAction.CycleSubtitles:
                        stream_cycle_channel(cur_stream, AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case EventAction.NextChapter:
                        if (cur_stream.InputContext->nb_chapters <= 1)
                        {
                            incr = 600.0;
                            goto do_seek;
                        }
                        seek_chapter(cur_stream, 1);
                        break;
                    case EventAction.PreviousChapter:
                        if (cur_stream.InputContext->nb_chapters <= 1)
                        {
                            incr = -600.0;
                            goto do_seek;
                        }
                        seek_chapter(cur_stream, -1);
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
                            if (pos < 0 && cur_stream.VideoStreamIndex >= 0)
                                pos = frame_queue_last_pos(cur_stream.PictureQueue);

                            if (pos < 0 && cur_stream.audio_stream >= 0)
                                pos = frame_queue_last_pos(cur_stream.AudioQueue);

                            if (pos < 0)
                                pos = cur_stream.InputContext->pb->pos; // TODO: ffmpeg.avio_tell(cur_stream.ic->pb); avio_tell not available here

                            if (cur_stream.InputContext->bit_rate != 0)
                                incr *= cur_stream.InputContext->bit_rate / 8.0;
                            else
                                incr *= 180000.0;

                            pos += incr;
                            stream_seek(cur_stream, (long)pos, (long)incr, Convert.ToBoolean(MediaSeekByBytes));
                        }
                        else
                        {
                            pos = get_master_clock(cur_stream);
                            if (double.IsNaN(pos))
                                pos = (double)cur_stream.seek_pos / ffmpeg.AV_TIME_BASE;
                            pos += incr;

                            if (cur_stream.InputContext->start_time != ffmpeg.AV_NOPTS_VALUE && pos < cur_stream.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE)
                                pos = cur_stream.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE;

                            stream_seek(cur_stream, (long)(pos * ffmpeg.AV_TIME_BASE), (long)(incr * ffmpeg.AV_TIME_BASE), Convert.ToBoolean(MediaSeekByBytes));
                        }
                        break;
                    case EventAction.AllocatePicture:
                        alloc_picture(cur_stream);
                        break;
                    default:
                        break;
                }
            }
        }

        #endregion

    }
}