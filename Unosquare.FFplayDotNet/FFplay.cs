namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    // https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c

    internal unsafe partial class FFplay
    {


        #region Properties
        internal uint sws_flags { get; set; } = (uint)ffmpeg.SWS_BICUBIC;
        internal AVInputFormat* file_iformat { get; set; }
        internal string input_filename { get; set; }
        internal string window_title { get; set; }
        internal int default_width { get; set; } = 640;
        internal int default_height { get; set; } = 480;
        internal int screen_width { get; set; } = 0;
        internal int screen_height { get; set; } = 0;
        internal bool audio_disable { get; set; }
        internal bool video_disable { get; set; }
        internal bool subtitle_disable { get; set; }
        internal string[] wanted_stream_spec = new string[(int)AVMediaType.AVMEDIA_TYPE_NB];
        internal int seek_by_bytes { get; set; } = -1;
        internal bool display_disable { get; set; }
        internal bool show_status { get; set; } = true;
        internal SyncMode av_sync_type { get; set; } = SyncMode.AV_SYNC_AUDIO_MASTER;
        internal long start_time { get; set; } = ffmpeg.AV_NOPTS_VALUE;
        internal long duration { get; set; } = ffmpeg.AV_NOPTS_VALUE;
        internal bool fast { get; set; } = false;
        internal bool genpts { get; set; } = false;
        internal bool lowres { get; set; } = false;
        internal int decoder_reorder_pts { get; set; } = -1;
        internal bool autoexit { get; set; }
        internal int loop { get; set; } = 1;
        internal int framedrop { get; set; } = -1;
        internal int infinite_buffer { get; set; } = -1;
        //internal ShowMode show_mode { get; set; } = ShowMode.SHOW_MODE_VIDEO;
        internal string audio_codec_name { get; set; }
        internal string subtitle_codec_name { get; set; }
        internal string video_codec_name { get; set; }
        internal long cursor_last_shown { get; set; }
        internal int cursor_hidden { get; set; } = 0;
        internal int autorotate { get; set; } = 1;
        internal bool is_full_screen { get; set; }
        internal long audio_callback_time { get; set; }
        internal SDL_Window window { get; set; }
        internal SDL_Renderer renderer { get; set; }
        internal int dummy { get; set; }
        internal double rdftspeed { get; set; } = 0.02;
        internal long last_time { get; set; } = 0;
        internal readonly InterruptCallbackDelegate decode_interrupt_delegate = new InterruptCallbackDelegate(decode_interrupt_cb);
        internal readonly LockManagerCallbackDelegate lock_manager_delegate = new LockManagerCallbackDelegate(lockmgr);
        #endregion

        internal AVPacket* flush_pkt = null;
        internal AVDictionary* format_opts = null;
        internal AVDictionary* codec_opts = null;

        public FFplay()
        {

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
            Helper.RegisterFFmpeg();
            ffmpeg.avformat_network_init();
            init_opts();

            AVInputFormat* inputFormat = null;

            if (string.IsNullOrWhiteSpace(fromatName) == false)
                inputFormat = ffmpeg.av_find_input_format(fromatName);

            var lockManagerCallback = new av_lockmgr_register_cb_func { Pointer = Marshal.GetFunctionPointerForDelegate(lock_manager_delegate) };
            if (ffmpeg.av_lockmgr_register(lockManagerCallback) != 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not initialize lock manager!\n");
                do_exit(null);
            }

            var vst = stream_open(filename, inputFormat);

            event_loop(vst);
        }

        private int PollEvent() { return 0; }

        private EventAction refresh_loop_wait_ev(VideoState vst)
        {
            double remaining_time = 0.0;
            //SDL_PumpEvents();
            while (PollEvent() == 0)
            {

                if (remaining_time > 0.0)
                    Thread.Sleep(TimeSpan.FromSeconds(remaining_time * ffmpeg.AV_TIME_BASE));

                remaining_time = REFRESH_RATE;
                if (!vst.paused || vst.force_refresh)
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

        private int packet_queue_put_private(PacketQueue q, AVPacket* pkt)
        {
            if (q.abort_request)
                return -1;

            var pkt1 = new MyAVPacketList();
            pkt1.pkt = pkt;
            pkt1.next = null;
            if (pkt == flush_pkt)
                q.serial++;

            pkt1.serial = q.serial;
            if (q.last_pkt == null)
                q.first_pkt = pkt1;
            else
                q.last_pkt.next = pkt1;

            q.last_pkt = pkt1;
            q.nb_packets++;
            q.size += pkt1.pkt->size; // + sizeof(*pkt1); // TODO: unsure how to do this or if needed
            q.duration += pkt1.pkt->duration;

            SDL_CondSignal(q.cond);
            return 0;
        }

        private int packet_queue_put(PacketQueue q, AVPacket* pkt)
        {
            int ret;
            SDL_LockMutex(q.mutex);
            ret = packet_queue_put_private(q, pkt);
            SDL_UnlockMutex(q.mutex);
            if (pkt != flush_pkt && ret < 0)
                ffmpeg.av_packet_unref(pkt);
            return ret;
        }

        private int packet_queue_put_nullpacket(PacketQueue q, int stream_index)
        {
            var pkt = new AVPacket();
            ffmpeg.av_init_packet(&pkt);

            pkt.data = null;
            pkt.size = 0;
            pkt.stream_index = stream_index;
            return packet_queue_put(q, &pkt);
        }

        static int packet_queue_init(PacketQueue q)
        {
            q.mutex = SDL_CreateMutex();
            q.cond = SDL_CreateCond();
            q.abort_request = true;
            return 0;
        }

        static void packet_queue_flush(PacketQueue q)
        {
            MyAVPacketList pkt;
            MyAVPacketList pkt1;

            SDL_LockMutex(q.mutex);

            do
            {
                pkt = q.first_pkt;
                pkt1 = pkt.next;
                ffmpeg.av_packet_unref(pkt.pkt);
            } while (pkt != null);

            q.last_pkt = null;
            q.first_pkt = null;
            q.nb_packets = 0;
            q.size = 0;
            q.duration = 0;
            SDL_UnlockMutex(q.mutex);
        }

        static void packet_queue_destroy(PacketQueue q)
        {
            packet_queue_flush(q);
            SDL_DestroyMutex(q.mutex);
            SDL_DestroyCond(q.cond);
        }

        static void packet_queue_abort(PacketQueue q)
        {
            SDL_LockMutex(q.mutex);
            q.abort_request = true;
            SDL_CondSignal(q.cond);
            SDL_UnlockMutex(q.mutex);
        }

        private void packet_queue_start(PacketQueue q)
        {
            SDL_LockMutex(q.mutex);
            q.abort_request = false;
            packet_queue_put_private(q, flush_pkt);
            SDL_UnlockMutex(q.mutex);
        }

        static int packet_queue_get(PacketQueue q, AVPacket* pkt, int block, ref int serial)
        {
            MyAVPacketList pkt1 = null;
            int ret = 0;

            SDL_LockMutex(q.mutex);
            for (;;)
            {
                if (q.abort_request)
                {
                    ret = -1;
                    break;
                }
                pkt1 = q.first_pkt;
                if (pkt1 != null)
                {
                    q.first_pkt = pkt1.next;
                    if (q.first_pkt == null)
                        q.last_pkt = null;
                    q.nb_packets--;
                    q.size -= pkt1.pkt->size; // + sizeof(*pkt1); // TODO: Verify
                    q.duration -= pkt1.pkt->duration;
                    pkt = pkt1.pkt;
                    if (serial != 0)
                        serial = pkt1.serial;
                    ret = 1;
                    break;
                }
                else if (block == 0)
                {
                    ret = 0;
                    break;
                }
                else
                {
                    SDL_CondWait(q.cond, q.mutex);
                }
            }
            SDL_UnlockMutex(q.mutex);
            return ret;
        }

        static void decoder_init(Decoder d, AVCodecContext* avctx, PacketQueue queue, SDL_cond empty_queue_cond)
        {
            d.avctx = avctx;
            d.queue = queue;
            d.empty_queue_cond = empty_queue_cond;
            d.start_pts = ffmpeg.AV_NOPTS_VALUE;
        }

        private int decoder_decode_frame(Decoder d, AVFrame* frame, AVSubtitle* sub)
        {
            int got_frame = 0;
            do
            {
                int ret = -1;
                if (d.queue.abort_request)
                    return -1;
                if (!d.packet_pending || d.queue.serial != d.pkt_serial)
                {
                    var pkt = new AVPacket();
                    do
                    {
                        if (d.queue.nb_packets == 0)
                            SDL_CondSignal(d.empty_queue_cond);
                        if (packet_queue_get(d.queue, &pkt, 1, ref d.pkt_serial) < 0)
                            return -1;
                        if (pkt.data == flush_pkt->data)
                        {
                            ffmpeg.avcodec_flush_buffers(d.avctx);
                            d.finished = false;
                            d.next_pts = d.start_pts;
                            d.next_pts_tb = d.start_pts_tb;
                        }
                    } while (pkt.data == flush_pkt->data || d.queue.serial != d.pkt_serial);
                    fixed (AVPacket* refPacket = &d.pkt)
                    {
                        ffmpeg.av_packet_unref(refPacket);
                    }

                    d.pkt_temp = d.pkt = pkt;
                    d.packet_pending = true;
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
                            if (decoder_reorder_pts == -1)
                            {
                                frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
                            }
                            else if (!Convert.ToBoolean(decoder_reorder_pts))
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
                    d.packet_pending = false;
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
                            d.packet_pending = false;
                    }
                    else
                    {
                        if (got_frame == 0)
                        {
                            d.packet_pending = false;
                            d.finished = Convert.ToBoolean(d.pkt_serial);
                        }
                    }
                }
            } while (!Convert.ToBoolean(got_frame) && !d.finished);
            return got_frame;
        }

        static void decoder_destroy(Decoder d)
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
            ffmpeg.av_frame_unref(vp.frame);
            fixed (AVSubtitle* vpsub = &vp.sub)
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
                f.queue[i].frame = ffmpeg.av_frame_alloc();

            return 0;
        }

        static void frame_queue_destory(FrameQueue f)
        {
            for (var i = 0; i < f.max_size; i++)
            {
                Frame vp = f.queue[i];
                frame_queue_unref_item(vp);
                fixed (AVFrame** frameRef = &vp.frame)
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
                   !f.pktq.abort_request)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);
            if (f.pktq.abort_request)
                return null;
            return f.queue[f.windex];
        }

        static Frame frame_queue_peek_readable(FrameQueue f)
        {
            SDL_LockMutex(f.mutex);
            while (f.size - f.rindex_shown <= 0 &&
                   !f.pktq.abort_request)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);
            if (f.pktq.abort_request)
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
            if (f.rindex_shown != 0 && fp.serial == f.pktq.serial)
                return fp.pos;
            else
                return -1;
        }

        static void decoder_abort(Decoder d, FrameQueue fq)
        {
            packet_queue_abort(d.queue);
            frame_queue_signal(fq);
            SDL_WaitThread(d.decoder_tid, null);
            d.decoder_tid = null;
            packet_queue_flush(d.queue);
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
                        AVPixelFormat.AV_PIX_FMT_BGRA, (int)sws_flags, null, null, null);
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

        private void video_image_display(VideoState vst)
        {
            var vp = new Frame();
            Frame sp = null;
            var rect = new SDL_Rect();

            vp = frame_queue_peek_last(vst.pictq);
            if (vp.bmp != null)
            {
                if (vst.subtitle_st != null)
                {
                    if (frame_queue_nb_remaining(vst.subpq) > 0)
                    {
                        sp = frame_queue_peek(vst.subpq);
                        if (vp.pts >= sp.pts + ((float)sp.sub.start_display_time / 1000))
                        {
                            if (!sp.uploaded)
                            {
                                byte** pixels = null;
                                int pitch = 0;

                                if (sp.width == 0 || sp.height == 0)
                                {
                                    sp.width = vp.width;
                                    sp.height = vp.height;
                                }

                                if (realloc_texture(vst.sub_texture, SDL_PIXELFORMAT_ARGB8888, sp.width, sp.height, SDL_BLENDMODE_BLEND, 1) < 0)
                                    return;

                                for (var i = 0; i < sp.sub.num_rects; i++)
                                {
                                    AVSubtitleRect* sub_rect = sp.sub.rects[i];
                                    sub_rect->x = ffmpeg.av_clip(sub_rect->x, 0, sp.width);
                                    sub_rect->y = ffmpeg.av_clip(sub_rect->y, 0, sp.height);
                                    sub_rect->w = ffmpeg.av_clip(sub_rect->w, 0, sp.width - sub_rect->x);
                                    sub_rect->h = ffmpeg.av_clip(sub_rect->h, 0, sp.height - sub_rect->y);

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

                                sp.uploaded = true;
                            }
                        }
                        else
                        {
                            sp = null;
                        }
                    }
                }

                calculate_display_rect(rect, vst.xleft, vst.ytop, vst.width, vst.height, vp.width, vp.height, vp.sar);

                if (!vp.uploaded)
                {
                    fixed (SwsContext** ctx = &vst.img_convert_ctx)
                    {
                        if (upload_texture(vp.bmp, vp.frame, ctx) < 0)
                            return;
                    }

                    vp.uploaded = true;
                }

                SDL_RenderCopy(renderer, vp.bmp, null, rect);

                if (sp != null)
                {
                    SDL_RenderCopy(renderer, vst.sub_texture, null, rect);

                    int i;
                    double xratio = (double)rect.w / (double)sp.width;
                    double yratio = (double)rect.h / (double)sp.height;
                    for (i = 0; i < sp.sub.num_rects; i++)
                    {
                        var sub_rect = sp.sub.rects[i];
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

        private static void stream_component_close(VideoState vst, int stream_index)
        {
            AVFormatContext* ic = vst.ic;
            AVCodecParameters* codecpar;
            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return;
            codecpar = ic->streams[stream_index]->codecpar;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    decoder_abort(vst.auddec, vst.sampq);
                    SDL_CloseAudio();
                    decoder_destroy(vst.auddec);
                    fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                    {
                        ffmpeg.swr_free(vst_swr_ctx);
                    }

                    ffmpeg.av_freep((void*)vst.audio_buf1);
                    vst.audio_buf1_size = 0;
                    vst.audio_buf = null;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    decoder_abort(vst.viddec, vst.pictq);
                    decoder_destroy(vst.viddec);
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    decoder_abort(vst.subdec, vst.subpq);
                    decoder_destroy(vst.subdec);
                    break;
                default:
                    break;
            }
            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_ALL;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    vst.audio_st = null;
                    vst.audio_stream = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vst.video_st = null;
                    vst.video_stream = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vst.subtitle_st = null;
                    vst.subtitle_stream = -1;
                    break;
                default:
                    break;
            }
        }

        static void stream_close(VideoState vst)
        {
            vst.abort_request = true;
            SDL_WaitThread(vst.read_tid, null);
            if (vst.audio_stream >= 0)
                stream_component_close(vst, vst.audio_stream);
            if (vst.video_stream >= 0)
                stream_component_close(vst, vst.video_stream);
            if (vst.subtitle_stream >= 0)
                stream_component_close(vst, vst.subtitle_stream);
            fixed (AVFormatContext** vstic = &vst.ic)
            {
                ffmpeg.avformat_close_input(vstic);
            }

            packet_queue_destroy(vst.videoq);
            packet_queue_destroy(vst.audioq);
            packet_queue_destroy(vst.subtitleq);
            frame_queue_destory(vst.pictq);
            frame_queue_destory(vst.sampq);
            frame_queue_destory(vst.subpq);
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
            ffmpeg.av_init_packet(flush_pkt);
            flush_pkt->data = (byte*)flush_pkt;

            var codecOpts = new AVDictionary();
            codec_opts = &codecOpts;

            var formatOpts = new AVDictionary();
            format_opts = &formatOpts;
        }

        private void uninit_opts()
        {
            ffmpeg.av_packet_unref(flush_pkt);

            fixed (AVDictionary** opts_ref = &format_opts)
                ffmpeg.av_dict_free(opts_ref);

            fixed (AVDictionary** opts_ref = &codec_opts)
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

        private void do_exit(VideoState vst)
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

        private int video_open(VideoState vst, Frame vp)
        {
            int w, h;
            if (vp != null && vp.width != 0)
                set_default_window_size(vp.width, vp.height, vp.sar);

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
                if (string.IsNullOrWhiteSpace(window_title) == false)
                    window_title = input_filename;
                if (is_full_screen)
                    flags |= SDL_WINDOW_FULLSCREEN_DESKTOP;

                window = SDL_CreateWindow(window_title, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, w, h, flags);
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

            vst.width = w;
            vst.height = h;
            return 0;
        }

        private void video_display(VideoState vst)
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

        static SyncMode get_master_sync_type(VideoState vst)
        {
            if (vst.av_sync_type == SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                if (vst.video_st != null)
                    return SyncMode.AV_SYNC_VIDEO_MASTER;
                else
                    return SyncMode.AV_SYNC_AUDIO_MASTER;
            }
            else if (vst.av_sync_type == SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                if (vst.audio_st != null)
                    return SyncMode.AV_SYNC_AUDIO_MASTER;
                else
                    return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
            }
            else
            {
                return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
            }
        }

        static double get_master_clock(VideoState vst)
        {
            double val;
            switch (get_master_sync_type(vst))
            {
                case SyncMode.AV_SYNC_VIDEO_MASTER:
                    val = get_clock(vst.vidclk);
                    break;
                case SyncMode.AV_SYNC_AUDIO_MASTER:
                    val = get_clock(vst.audclk);
                    break;
                default:
                    val = get_clock(vst.extclk);
                    break;
            }
            return val;
        }

        static void check_external_clock_speed(VideoState vst)
        {
            if (vst.video_stream >= 0 && vst.videoq.nb_packets <= EXTERNAL_CLOCK_MIN_FRAMES ||
                vst.audio_stream >= 0 && vst.audioq.nb_packets <= EXTERNAL_CLOCK_MIN_FRAMES)
            {
                set_clock_speed(vst.extclk, Math.Max(EXTERNAL_CLOCK_SPEED_MIN, vst.extclk.speed - EXTERNAL_CLOCK_SPEED_STEP));
            }
            else if ((vst.video_stream < 0 || vst.videoq.nb_packets > EXTERNAL_CLOCK_MAX_FRAMES) &&
                     (vst.audio_stream < 0 || vst.audioq.nb_packets > EXTERNAL_CLOCK_MAX_FRAMES))
            {
                set_clock_speed(vst.extclk, Math.Min(EXTERNAL_CLOCK_SPEED_MAX, vst.extclk.speed + EXTERNAL_CLOCK_SPEED_STEP));
            }
            else
            {
                double speed = vst.extclk.speed;
                if (speed != 1.0)
                    set_clock_speed(vst.extclk, speed + EXTERNAL_CLOCK_SPEED_STEP * (1.0 - speed) / Math.Abs(1.0 - speed));
            }
        }

        static void stream_seek(VideoState vst, long pos, long rel, bool seek_by_bytes)
        {
            if (!vst.seek_req)
            {
                vst.seek_pos = pos;
                vst.seek_rel = rel;
                vst.seek_flags &= ~ffmpeg.AVSEEK_FLAG_BYTE;
                if (seek_by_bytes) vst.seek_flags |= ffmpeg.AVSEEK_FLAG_BYTE;
                vst.seek_req = true;
                SDL_CondSignal(vst.continue_read_thread);
            }
        }

        static void stream_toggle_pause(VideoState vst)
        {
            if (vst.paused)
            {
                vst.frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - vst.vidclk.last_updated;
                if (vst.read_pause_return != AVERROR_NOTSUPP)
                {
                    vst.vidclk.paused = false;
                }
                set_clock(vst.vidclk, get_clock(vst.vidclk), vst.vidclk.serial);
            }
            set_clock(vst.extclk, get_clock(vst.extclk), vst.extclk.serial);
            vst.paused = vst.audclk.paused = vst.vidclk.paused = vst.extclk.paused = !vst.paused;
        }

        static void toggle_pause(VideoState vst)
        {
            stream_toggle_pause(vst);
            vst.step = false;
        }

        static void toggle_mute(VideoState vst)
        {
            vst.muted = !vst.muted;
        }

        static void update_volume(VideoState vst, int sign, int step)
        {
            vst.audio_volume = ffmpeg.av_clip(vst.audio_volume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        static void step_to_next_frame(VideoState vst)
        {
            if (vst.paused)
                stream_toggle_pause(vst);
            vst.step = true;
        }

        static double compute_target_delay(double delay, VideoState vst)
        {
            double sync_threshold, diff = 0;
            if (get_master_sync_type(vst) != SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                diff = get_clock(vst.vidclk) - get_master_clock(vst);
                sync_threshold = Math.Max(AV_SYNC_THRESHOLD_MIN, Math.Min(AV_SYNC_THRESHOLD_MAX, delay));
                if (!double.IsNaN(diff) && Math.Abs(diff) < vst.max_frame_duration)
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

        static double vp_duration(VideoState vst, Frame vp, Frame nextvp)
        {
            if (vp.serial == nextvp.serial)
            {
                double duration = nextvp.pts - vp.pts;
                if (double.IsNaN(duration) || duration <= 0 || duration > vst.max_frame_duration)
                    return vp.duration;
                else
                    return duration;
            }
            else
            {
                return 0.0;
            }
        }

        static void update_video_pts(VideoState vst, double pts, long pos, int serial)
        {
            set_clock(vst.vidclk, pts, serial);
            sync_clock_to_slave(vst.extclk, vst.vidclk);
        }

        private void alloc_picture(VideoState vst)
        {
            var vp = new Frame();
            uint sdl_format;
            vp = vst.pictq.queue[vst.pictq.windex];
            video_open(vst, vp);
            if (vp.format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P)
                sdl_format = SDL_PIXELFORMAT_YV12;
            else
                sdl_format = SDL_PIXELFORMAT_ARGB8888;

            if (realloc_texture(vp.bmp, sdl_format, vp.width, vp.height, SDL_BLENDMODE_NONE, 0) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL,
                       $"Error: the video system does not support an image\n" +
                                $"size of {vp.width}x{vp.height} pixels. Try using -lowres or -vf \"scale=w:h\"\n" +
                                "to reduce the image size.\n");
                do_exit(vst);
            }

            SDL_LockMutex(vst.pictq.mutex);
            vp.allocated = true;
            SDL_CondSignal(vst.pictq.cond);
            SDL_UnlockMutex(vst.pictq.mutex);
        }

        private void video_refresh(VideoState vst, ref double remaining_time)
        {
            double time;
            var sp = new Frame();
            var sp2 = new Frame();

            if (!vst.paused && get_master_sync_type(vst) == SyncMode.AV_SYNC_EXTERNAL_CLOCK && vst.realtime)
                check_external_clock_speed(vst);

            if (vst.video_st != null)
            {
                retry:
                if (frame_queue_nb_remaining(vst.pictq) == 0)
                {
                    // nothing to do, no picture to display in the queue
                }
                else
                {
                    double last_duration, duration, delay;

                    var lastvp = frame_queue_peek_last(vst.pictq);
                    var vp = frame_queue_peek(vst.pictq);
                    if (vp.serial != vst.videoq.serial)
                    {
                        frame_queue_next(vst.pictq);
                        goto retry;
                    }
                    if (lastvp.serial != vp.serial)
                        vst.frame_timer = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (vst.paused)
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
                    SDL_LockMutex(vst.pictq.mutex);
                    if (!double.IsNaN(vp.pts))
                        update_video_pts(vst, vp.pts, vp.pos, vp.serial);
                    SDL_UnlockMutex(vst.pictq.mutex);
                    if (frame_queue_nb_remaining(vst.pictq) > 1)
                    {
                        var nextvp = frame_queue_peek_next(vst.pictq);
                        duration = vp_duration(vst, vp, nextvp);
                        if (!vst.step && (framedrop > 0 || (framedrop != 0 && get_master_sync_type(vst) != SyncMode.AV_SYNC_VIDEO_MASTER)) && time > vst.frame_timer + duration)
                        {
                            vst.frame_drops_late++;
                            frame_queue_next(vst.pictq);
                            goto retry;
                        }
                    }
                    if (vst.subtitle_st != null)
                    {
                        while (frame_queue_nb_remaining(vst.subpq) > 0)
                        {
                            sp = frame_queue_peek(vst.subpq);
                            if (frame_queue_nb_remaining(vst.subpq) > 1)
                                sp2 = frame_queue_peek_next(vst.subpq);
                            else
                                sp2 = null;

                            if (sp.serial != vst.subtitleq.serial
                                    || (vst.vidclk.pts > (sp.pts + ((float)sp.sub.end_display_time / 1000)))
                                    || (sp2 != null && vst.vidclk.pts > (sp2.pts + ((float)sp2.sub.start_display_time / 1000))))
                            {
                                if (sp.uploaded)
                                {
                                    for (var i = 0; i < sp.sub.num_rects; i++)
                                    {
                                        var sub_rect = sp.sub.rects[i];
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
                                frame_queue_next(vst.subpq);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    frame_queue_next(vst.pictq);
                    vst.force_refresh = true;
                    if (vst.step && !vst.paused)
                        stream_toggle_pause(vst);
                }
                display:
                if (vst.force_refresh && vst.pictq.rindex_shown != 0)
                    video_display(vst);
            }

            vst.force_refresh = false;
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
                    if (vst.audio_st != null)
                        aqsize = vst.audioq.size;
                    if (vst.video_st != null)
                        vqsize = vst.videoq.size;
                    if (vst.subtitle_st != null)
                        sqsize = vst.subtitleq.size;
                    av_diff = 0;
                    if (vst.audio_st != null && vst.video_st != null)
                        av_diff = get_clock(vst.audclk) - get_clock(vst.vidclk);
                    else if (vst.video_st != null)
                        av_diff = get_master_clock(vst) - get_clock(vst.vidclk);
                    else if (vst.audio_st != null)
                        av_diff = get_master_clock(vst) - get_clock(vst.audclk);

                    var mode = (vst.audio_st != null && vst.video_st != null) ? "A-V" : (vst.video_st != null ? "M-V" : (vst.audio_st != null ? "M-A" : "   "));
                    var faultyDts = vst.video_st != null ? vst.viddec.avctx->pts_correction_num_faulty_dts : 0;
                    var faultyPts = vst.video_st != null ? vst.viddec.avctx->pts_correction_num_faulty_pts : 0;

                    ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO,
                           $"{get_master_clock(vst)} {mode}:{av_diff} fd={vst.frame_drops_early + vst.frame_drops_late} aq={aqsize / 1024}KB vq={vqsize / 1024}KB sq={sqsize}dB f={faultyDts} / {faultyPts}\r");


                    // fflush(stdout);
                    last_time = cur_time;
                }
            }
        }

        static int queue_picture(VideoState vst, AVFrame* src_frame, double pts, double duration, long pos, int serial)
        {
            var vp = frame_queue_peek_writable(vst.pictq);

            Debug.WriteLine($"frame_type={ffmpeg.av_get_picture_type_char(src_frame->pict_type)} pts={pts}");

            if (vp == null)
                return -1;

            vp.sar = src_frame->sample_aspect_ratio;
            vp.uploaded = false;

            if (vp.bmp == null || !vp.allocated ||
                vp.width != src_frame->width ||
                vp.height != src_frame->height ||
                vp.format != src_frame->format)
            {
                var ev = new SDL_Event();
                vp.allocated = false;
                vp.width = src_frame->width;
                vp.height = src_frame->height;
                vp.format = src_frame->format;
                ev.type = FF_ALLOC_EVENT;
                ev.user_data1 = vst;
                SDL_PushEvent(ev);
                SDL_LockMutex(vst.pictq.mutex);

                while (!vp.allocated && !vst.videoq.abort_request)
                {
                    SDL_CondWait(vst.pictq.cond, vst.pictq.mutex);
                }
                if (vst.videoq.abort_request && SDL_PeepEvents(ev, 1, SDL_GETEVENT, FF_ALLOC_EVENT, FF_ALLOC_EVENT) != 1)
                {
                    while (!vp.allocated && !vst.abort_request)
                    {
                        SDL_CondWait(vst.pictq.cond, vst.pictq.mutex);
                    }
                }
                SDL_UnlockMutex(vst.pictq.mutex);
                if (vst.videoq.abort_request)
                    return -1;
            }
            if (vp.bmp != null)
            {
                vp.pts = pts;
                vp.duration = duration;
                vp.pos = pos;
                vp.serial = serial;
                ffmpeg.av_frame_move_ref(vp.frame, src_frame);
                frame_queue_push(vst.pictq);
            }
            return 0;
        }

        private int get_video_frame(VideoState vst, AVFrame* frame)
        {
            int got_picture;
            if ((got_picture = decoder_decode_frame(vst.viddec, frame, null)) < 0)
                return -1;

            if (got_picture != 0)
            {
                var dpts = double.NaN;
                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    dpts = ffmpeg.av_q2d(vst.video_st->time_base) * frame->pts;
                frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(vst.ic, vst.video_st, frame);
                if (framedrop > 0 || (framedrop != 0 && get_master_sync_type(vst) != SyncMode.AV_SYNC_VIDEO_MASTER))
                {
                    if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double diff = dpts - get_master_clock(vst);
                        if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD &&
                            diff - vst.frame_last_filter_delay < 0 &&
                            vst.viddec.pkt_serial == vst.vidclk.serial &&
                            vst.videoq.nb_packets != 0)
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

        static int synchronize_audio(VideoState vst, int nb_samples)
        {
            int wanted_nb_samples = nb_samples;

            /* if not master, then we try to remove or add samples to correct the clock */
            if (get_master_sync_type(vst) != SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                double diff, avg_diff;
                int min_nb_samples, max_nb_samples;

                diff = get_clock(vst.audclk) - get_master_clock(vst);

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
                            wanted_nb_samples = nb_samples + (int)(diff * vst.audio_src.freq);
                            min_nb_samples = ((nb_samples * (100 - SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            max_nb_samples = ((nb_samples * (100 + SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            wanted_nb_samples = ffmpeg.av_clip(wanted_nb_samples, min_nb_samples, max_nb_samples);
                        }
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wanted_nb_samples - nb_samples} apts={vst.audio_clock} {vst.audio_diff_threshold}\n");
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

            return wanted_nb_samples;
        }

        private int audio_decode_frame(VideoState vst)
        {
            int data_size, resampled_data_size;
            long dec_channel_layout;
            double audio_clock0;
            int wanted_nb_samples;

            if (vst.paused)
                return -1;

            Frame af = null;
            do
            {

                while (frame_queue_nb_remaining(vst.sampq) == 0)
                {
                    if ((ffmpeg.av_gettime_relative() - audio_callback_time) > 1000000L * vst.audio_hw_buf_size / vst.audio_tgt.bytes_per_sec / 2)
                        return -1;
                    Thread.Sleep(1); //ffmpeg.av_usleep(1000);
                }

                af = frame_queue_peek_readable(vst.sampq);
                if (af == null)
                    return -1;

                frame_queue_next(vst.sampq);
            } while (af.serial != vst.audioq.serial);

            data_size = ffmpeg.av_samples_get_buffer_size(null, ffmpeg.av_frame_get_channels(af.frame),
                                                   af.frame->nb_samples,
                                                   (AVSampleFormat)af.frame->format, 1);

            dec_channel_layout =
                (af.frame->channel_layout != 0 && ffmpeg.av_frame_get_channels(af.frame) == ffmpeg.av_get_channel_layout_nb_channels(af.frame->channel_layout)) ?
                    Convert.ToInt64(af.frame->channel_layout) :
                    ffmpeg.av_get_default_channel_layout(ffmpeg.av_frame_get_channels(af.frame));

            wanted_nb_samples = synchronize_audio(vst, af.frame->nb_samples);

            if (af.frame->format != (int)vst.audio_src.fmt ||
                dec_channel_layout != vst.audio_src.channel_layout ||
                af.frame->sample_rate != vst.audio_src.freq ||
                (wanted_nb_samples != af.frame->nb_samples && vst.swr_ctx == null))
            {
                fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                    ffmpeg.swr_free(vst_swr_ctx);

                vst.swr_ctx = ffmpeg.swr_alloc_set_opts(
                    null,
                    vst.audio_tgt.channel_layout, vst.audio_tgt.fmt, vst.audio_tgt.freq,
                    dec_channel_layout, (AVSampleFormat)af.frame->format, af.frame->sample_rate,
                    0, null);
                if (vst.swr_ctx == null || ffmpeg.swr_init(vst.swr_ctx) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"Cannot create sample rate converter for conversion of {af.frame->sample_rate} Hz {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)af.frame->format)} " +
                           "{ffmpeg.av_frame_get_channels(af.frame)} channels to {vst.audio_tgt.freq} Hz {ffmpeg.av_get_sample_fmt_name(vst.audio_tgt.fmt)} {vst.audio_tgt.channels} " +
                           "channels!\n");

                    fixed (SwrContext** vst_swr_ctx = &vst.swr_ctx)
                        ffmpeg.swr_free(vst_swr_ctx);

                    return -1;
                }

                vst.audio_src.channel_layout = dec_channel_layout;
                vst.audio_src.channels = ffmpeg.av_frame_get_channels(af.frame);
                vst.audio_src.freq = af.frame->sample_rate;
                vst.audio_src.fmt = (AVSampleFormat)af.frame->format;
            }

            if (vst.swr_ctx != null)
            {
                var in_buffer = af.frame->extended_data;
                var out_buffer = vst.audio_buf1;
                int out_count = wanted_nb_samples * vst.audio_tgt.freq / af.frame->sample_rate + 256;
                int out_size = ffmpeg.av_samples_get_buffer_size(null, vst.audio_tgt.channels, out_count, vst.audio_tgt.fmt, 0);
                int len2;

                if (out_size < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size() failed\n");
                    return -1;
                }

                if (wanted_nb_samples != af.frame->nb_samples)
                {
                    if (ffmpeg.swr_set_compensation(vst.swr_ctx, (wanted_nb_samples - af.frame->nb_samples) * vst.audio_tgt.freq / af.frame->sample_rate,
                                                wanted_nb_samples * vst.audio_tgt.freq / af.frame->sample_rate) < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_set_compensation() failed\n");
                        return -1;
                    }
                }
                fixed (uint* vst_audio_buf1_size = &vst.audio_buf1_size)
                    ffmpeg.av_fast_malloc((void*)vst.audio_buf1, vst_audio_buf1_size, (ulong)out_size);

                if (vst.audio_buf1 == null)
                    return ffmpeg.AVERROR_ENOMEM;

                len2 = ffmpeg.swr_convert(vst.swr_ctx, &out_buffer, out_count, in_buffer, af.frame->nb_samples);
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
                resampled_data_size = len2 * vst.audio_tgt.channels * ffmpeg.av_get_bytes_per_sample(vst.audio_tgt.fmt);
            }
            else
            {
                var x = af.frame->data[0];

                vst.audio_buf = af.frame->data[0];
                resampled_data_size = data_size;
            }

            audio_clock0 = vst.audio_clock;
            /* update the audio clock with the pts */
            if (!double.IsNaN(af.pts))
                vst.audio_clock = af.pts + (double)af.frame->nb_samples / af.frame->sample_rate;
            else
                vst.audio_clock = double.NaN;

            vst.audio_clock_serial = af.serial;

            return resampled_data_size;
        }

        private void sdl_audio_callback(VideoState vst, byte* stream, int len)
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
                        vst.audio_buf_size = Convert.ToUInt32(SDL_AUDIO_MIN_BUFFER_SIZE / vst.audio_tgt.frame_size * vst.audio_tgt.frame_size);
                    }
                    else
                    {
                        vst.audio_buf_size = Convert.ToUInt32(audio_size);
                    }

                    vst.audio_buf_index = 0;
                }
                len1 = Convert.ToInt32(vst.audio_buf_size - vst.audio_buf_index);
                if (len1 > len) len1 = len;

                if (!vst.muted && vst.audio_buf != null && vst.audio_volume == SDL_MIX_MAXVOLUME)
                {
                    ffmpeg.memcpy(stream, vst.audio_buf + vst.audio_buf_index, len1);
                }
                else
                {
                    ffmpeg.memset(stream, 0, len1);
                    if (!vst.muted && vst.audio_buf != null)
                        SDL_MixAudio(stream, vst.audio_buf + vst.audio_buf_index, len1, vst.audio_volume);
                }

                len -= len1;
                stream += len1;
                vst.audio_buf_index += len1;
            }

            vst.audio_write_buf_size = Convert.ToInt32(vst.audio_buf_size - vst.audio_buf_index);
            if (!double.IsNaN(vst.audio_clock))
            {
                set_clock_at(vst.audclk, vst.audio_clock - (double)(2 * vst.audio_hw_buf_size + vst.audio_write_buf_size) / vst.audio_tgt.bytes_per_sec, vst.audio_clock_serial, audio_callback_time / 1000000.0);
                sync_clock_to_slave(vst.extclk, vst.audclk);
            }
        }

        private int audio_open(VideoState vst, long wanted_channel_layout, int wanted_nb_channels, int wanted_sample_rate, AudioParams audio_hw_params)
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

            audio_hw_params.fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            audio_hw_params.freq = spec.freq;
            audio_hw_params.channel_layout = wanted_channel_layout;
            audio_hw_params.channels = spec.channels;
            audio_hw_params.frame_size = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params.channels, 1, audio_hw_params.fmt, 1);
            audio_hw_params.bytes_per_sec = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params.channels, audio_hw_params.freq, audio_hw_params.fmt, 1);

            if (audio_hw_params.bytes_per_sec <= 0 || audio_hw_params.frame_size <= 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size failed\n");
                return -1;
            }

            return spec.size;
        }

        static bool is_realtime(AVFormatContext* s)
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

        static bool stream_has_enough_packets(AVStream* st, int stream_id, PacketQueue queue)
        {
            return
                (stream_id < 0) ||
                (queue.abort_request) ||
                ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) ||
                queue.nb_packets > MIN_FRAMES && (queue.duration == 0 || ffmpeg.av_q2d(st->time_base) * queue.duration > 1.0);
        }

        static int decode_interrupt_cb(void* opaque)
        {
            var vst = GCHandle.FromIntPtr(new IntPtr(opaque)).Target as VideoState;
            return vst.abort_request ? 1 : 0;
        }

        private int decoder_start(Decoder d, Func<VideoState, int> fn, VideoState vst)
        {
            packet_queue_start(d.queue);
            d.decoder_tid = SDL_CreateThread(fn, vst);
            if (d.decoder_tid == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"SDL_CreateThread(): {SDL_GetError()}\n");
                return ffmpeg.AVERROR_ENOMEM;
            }

            return 0;
        }

        private int video_thread(VideoState vst)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            double pts;
            double duration;
            int ret;
            var tb = vst.video_st->time_base;
            var frame_rate = ffmpeg.av_guess_frame_rate(vst.ic, vst.video_st, null);

            while (true)
            {
                ret = get_video_frame(vst, frame);
                if (ret < 0)
                    break;

                if (ret == 0)
                    continue;

                duration = (frame_rate.num != 0 && frame_rate.den != 0 ? ffmpeg.av_q2d(new AVRational { num = frame_rate.den, den = frame_rate.num }) : 0);
                pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
                ret = queue_picture(vst, frame, pts, duration, ffmpeg.av_frame_get_pkt_pos(frame), vst.viddec.pkt_serial);
                ffmpeg.av_frame_unref(frame);


                if (ret < 0)
                    break;
            }

            ffmpeg.av_frame_free(&frame);
            return 0;
        }

        private int subtitle_thread(VideoState vst)
        {
            Frame sp = null;
            int got_subtitle;
            double pts;

            while (true)
            {
                sp = frame_queue_peek_writable(vst.subpq);
                if (sp == null) return 0;

                fixed (AVSubtitle* sp_sub = &sp.sub)
                    got_subtitle = decoder_decode_frame(vst.subdec, null, sp_sub);

                if (got_subtitle < 0) break;

                pts = 0;

                if (got_subtitle != 0 && sp.sub.format == 0)
                {
                    if (sp.sub.pts != ffmpeg.AV_NOPTS_VALUE)
                        pts = sp.sub.pts / (double)ffmpeg.AV_TIME_BASE;

                    sp.pts = pts;
                    sp.serial = vst.subdec.pkt_serial;
                    sp.width = vst.subdec.avctx->width;
                    sp.height = vst.subdec.avctx->height;
                    sp.uploaded = false;

                    /* now we can update the picture count */
                    frame_queue_push(vst.subpq);
                }
                else if (got_subtitle != 0)
                {
                    fixed (AVSubtitle* sp_sub = &sp.sub)
                        ffmpeg.avsubtitle_free(sp_sub);
                }
            }

            return 0;
        }

        private int audio_thread(VideoState vst)
        {
            var frame = ffmpeg.av_frame_alloc();
            Frame af;
            int got_frame = 0;
            AVRational tb;
            int ret = 0;

            do
            {
                got_frame = decoder_decode_frame(vst.auddec, frame, null);

                if (got_frame < 0) break;

                if (got_frame != 0)
                {
                    tb = new AVRational { num = 1, den = frame->sample_rate };
                    af = frame_queue_peek_writable(vst.sampq);
                    if (af == null) break;

                    af.pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
                    af.pos = ffmpeg.av_frame_get_pkt_pos(frame);
                    af.serial = vst.auddec.pkt_serial;
                    af.duration = ffmpeg.av_q2d(new AVRational { num = frame->nb_samples, den = frame->sample_rate });

                    ffmpeg.av_frame_move_ref(af.frame, frame);
                    frame_queue_push(vst.sampq);

                }
            } while (ret >= 0 || ret == ffmpeg.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF);

            ffmpeg.av_frame_free(&frame);
            return ret;
        }

        private int stream_component_open(VideoState vst, int stream_index)
        {
            var ic = vst.ic;
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
                case AVMediaType.AVMEDIA_TYPE_AUDIO: vst.last_audio_stream = stream_index; forced_codec_name = audio_codec_name; break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: vst.last_subtitle_stream = stream_index; forced_codec_name = subtitle_codec_name; break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO: vst.last_video_stream = stream_index; forced_codec_name = video_codec_name; break;
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

            var opts = filter_codec_opts(codec_opts, avctx->codec_id, ic, ic->streams[stream_index], codec);

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

            vst.eof = false;
            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_DEFAULT;

            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    if ((ret = audio_open(vst, channel_layout, nb_channels, sample_rate, vst.audio_tgt)) < 0)
                        goto fail;

                    vst.audio_hw_buf_size = ret;
                    vst.audio_src = vst.audio_tgt;
                    vst.audio_buf_size = 0;
                    vst.audio_buf_index = 0;
                    vst.audio_diff_avg_coef = Math.Exp(Math.Log(0.01) / AUDIO_DIFF_AVG_NB);
                    vst.audio_diff_avg_count = 0;
                    vst.audio_diff_threshold = (double)(vst.audio_hw_buf_size) / vst.audio_tgt.bytes_per_sec;
                    vst.audio_stream = stream_index;
                    vst.audio_st = ic->streams[stream_index];

                    decoder_init(vst.auddec, avctx, vst.audioq, vst.continue_read_thread);

                    if ((vst.ic->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                        vst.ic->iformat->read_seek.Pointer == IntPtr.Zero)
                    {
                        vst.auddec.start_pts = vst.audio_st->start_time;
                        vst.auddec.start_pts_tb = vst.audio_st->time_base;
                    }

                    if ((ret = decoder_start(vst.auddec, audio_thread, vst)) < 0)
                        goto final;

                    SDL_PauseAudio(0);
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vst.video_stream = stream_index;
                    vst.video_st = ic->streams[stream_index];
                    decoder_init(vst.viddec, avctx, vst.videoq, vst.continue_read_thread);
                    if ((ret = decoder_start(vst.viddec, video_thread, vst)) < 0)
                        goto final;
                    vst.queue_attachments_req = true;
                    break;

                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vst.subtitle_stream = stream_index;
                    vst.subtitle_st = ic->streams[stream_index];
                    decoder_init(vst.subdec, avctx, vst.subtitleq, vst.continue_read_thread);
                    if ((ret = decoder_start(vst.subdec, subtitle_thread, vst)) < 0)
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

        private int read_thread(VideoState vst)
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
            vst.last_video_stream = vst.video_stream = -1;
            vst.last_audio_stream = vst.audio_stream = -1;
            vst.last_subtitle_stream = vst.subtitle_stream = -1;
            vst.eof = false;
            ic = ffmpeg.avformat_alloc_context();
            if (ic == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not allocate context.\n");
                ret = ffmpeg.AVERROR_ENOMEM;
                goto fail;
            }

            ic->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(decode_interrupt_delegate) };
            ic->interrupt_callback.opaque = (void*)vst.Handle.AddrOfPinnedObject();

            fixed (AVDictionary** format_opts_ref = &format_opts)
            {
                if (ffmpeg.av_dict_get(format_opts, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE) == null)
                {
                    ffmpeg.av_dict_set(format_opts_ref, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
                    scan_all_pmts_set = true;
                }

                err = ffmpeg.avformat_open_input(&ic, vst.filename, vst.iformat, format_opts_ref);
                if (err < 0)
                {
                    Debug.WriteLine($"Error in read_thread. File '{vst.filename}'. {err}");
                    ret = -1;
                    goto fail;
                }

                if (scan_all_pmts_set)
                    ffmpeg.av_dict_set(format_opts_ref, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE);

                t = ffmpeg.av_dict_get(format_opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX);

            }

            if (t != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Marshal.PtrToStringAnsi(new IntPtr(t->key))} not found.\n");
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            vst.ic = ic;
            if (genpts)
                ic->flags |= ffmpeg.AVFMT_FLAG_GENPTS;

            ffmpeg.av_format_inject_global_side_data(ic);

            opts = setup_find_stream_info_opts(ic, codec_opts);
            orig_nb_streams = Convert.ToInt32(ic->nb_streams);
            err = ffmpeg.avformat_find_stream_info(ic, opts);
            for (i = 0; i < orig_nb_streams; i++)
                ffmpeg.av_dict_free(&opts[i]);

            ffmpeg.av_freep(&opts);

            if (err < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{vst.filename}: could not find codec parameters\n");
                ret = -1;
                goto fail;
            }

            if (ic->pb != null)
                ic->pb->eof_reached = 0; // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end

            var formatName = Marshal.PtrToStringAnsi(new IntPtr(ic->iformat->name));
            var isDiscontinuous = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) == 0;

            // seek by byes only for continuous ogg vorbis
            if (seek_by_bytes < 0)
                seek_by_bytes = !isDiscontinuous && formatName.Equals("ogg") ? 1 : 0;

            vst.max_frame_duration = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0 ? 10.0 : 3600.0;

            if (string.IsNullOrWhiteSpace(window_title))
            {
                t = ffmpeg.av_dict_get(ic->metadata, "title", null, 0);
                if (t != null)
                    window_title = $"{Marshal.PtrToStringAnsi(new IntPtr(t->value))} - {input_filename}";
            }


            if (start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                long timestamp;
                timestamp = start_time;
                if (ic->start_time != ffmpeg.AV_NOPTS_VALUE)
                    timestamp += ic->start_time;

                ret = ffmpeg.avformat_seek_file(ic, -1, long.MinValue, timestamp, long.MaxValue, 0);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{vst.filename}: could not seek to position {((double)timestamp / ffmpeg.AV_TIME_BASE)}\n");
                }
            }

            vst.realtime = is_realtime(ic);
            if (show_status)
                ffmpeg.av_dump_format(ic, 0, vst.filename, 0);

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

            if (!video_disable)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);

            if (!audio_disable)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        null, 0);

            if (!video_disable && !subtitle_disable)
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

            if (vst.video_stream < 0 && vst.audio_stream < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Failed to open file '{vst.filename}' or configure filtergraph\n");
                ret = -1;
                goto fail;
            }

            if (infinite_buffer < 0 && vst.realtime)
                infinite_buffer = 1;

            while (true)
            {
                if (vst.abort_request)
                    break;

                if (Convert.ToInt32(vst.paused) != vst.last_paused)
                {
                    vst.last_paused = Convert.ToInt32(vst.paused);

                    if (vst.paused)
                        vst.read_pause_return = ffmpeg.av_read_pause(ic);
                    else
                        ffmpeg.av_read_play(ic);
                }

                if (vst.paused &&
                        (formatName.Equals("rtsp") ||
                         (ic->pb != null && input_filename.StartsWith("mmsh:"))))
                {
                    SDL_Delay(10);
                    continue;
                }

                if (vst.seek_req)
                {
                    long seek_target = vst.seek_pos;
                    long seek_min = vst.seek_rel > 0 ? seek_target - vst.seek_rel + 2 : long.MinValue;
                    long seek_max = vst.seek_rel < 0 ? seek_target - vst.seek_rel - 2 : long.MaxValue;
                    // FIXME the +-2 is due to rounding being not done in the correct direction in generation
                    //      of the seek_pos/seek_rel variables
                    ret = ffmpeg.avformat_seek_file(vst.ic, -1, seek_min, seek_target, seek_max, vst.seek_flags);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               $"{Encoding.GetEncoding(0).GetString(vst.ic->filename)}: error while seeking\n");
                    }
                    else
                    {
                        if (vst.audio_stream >= 0)
                        {
                            packet_queue_flush(vst.audioq);
                            packet_queue_put(vst.audioq, flush_pkt);
                        }
                        if (vst.subtitle_stream >= 0)
                        {
                            packet_queue_flush(vst.subtitleq);
                            packet_queue_put(vst.subtitleq, flush_pkt);
                        }
                        if (vst.video_stream >= 0)
                        {
                            packet_queue_flush(vst.videoq);
                            packet_queue_put(vst.videoq, flush_pkt);
                        }
                        if ((vst.seek_flags & ffmpeg.AVSEEK_FLAG_BYTE) != 0)
                        {
                            set_clock(vst.extclk, double.NaN, 0);
                        }
                        else
                        {
                            set_clock(vst.extclk, seek_target / (double)ffmpeg.AV_TIME_BASE, 0);
                        }
                    }

                    vst.seek_req = false;
                    vst.queue_attachments_req = true;
                    vst.eof = false;
                    if (vst.paused)
                        step_to_next_frame(vst);
                }
                if (vst.queue_attachments_req)
                {
                    if (vst.video_st != null && (vst.video_st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        var copy = new AVPacket();
                        if ((ret = ffmpeg.av_copy_packet(&copy, &vst.video_st->attached_pic)) < 0)
                            goto fail;

                        packet_queue_put(vst.videoq, &copy);
                        packet_queue_put_nullpacket(vst.videoq, vst.video_stream);
                    }

                    vst.queue_attachments_req = false;
                }
                if (infinite_buffer < 1 &&
                      (vst.audioq.size + vst.videoq.size + vst.subtitleq.size > MAX_QUEUE_SIZE
                    || (stream_has_enough_packets(vst.audio_st, vst.audio_stream, vst.audioq) &&
                        stream_has_enough_packets(vst.video_st, vst.video_stream, vst.videoq) &&
                        stream_has_enough_packets(vst.subtitle_st, vst.subtitle_stream, vst.subtitleq))))
                {
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vst.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                if (!vst.paused &&
                    (vst.audio_st == null || (vst.auddec.finished == Convert.ToBoolean(vst.audioq.serial) && frame_queue_nb_remaining(vst.sampq) == 0)) &&
                    (vst.video_st == null || (vst.viddec.finished == Convert.ToBoolean(vst.videoq.serial) && frame_queue_nb_remaining(vst.pictq) == 0)))
                {
                    if (loop != 1 && (loop == 0 || --loop == 0))
                    {
                        stream_seek(vst, start_time != ffmpeg.AV_NOPTS_VALUE ? start_time : 0, 0, false);
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
                    if ((ret == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) && !vst.eof)
                    {
                        if (vst.video_stream >= 0)
                            packet_queue_put_nullpacket(vst.videoq, vst.video_stream);
                        if (vst.audio_stream >= 0)
                            packet_queue_put_nullpacket(vst.audioq, vst.audio_stream);
                        if (vst.subtitle_stream >= 0)
                            packet_queue_put_nullpacket(vst.subtitleq, vst.subtitle_stream);

                        vst.eof = true;
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
                    vst.eof = false;
                }

                stream_start_time = ic->streams[pkt->stream_index]->start_time;
                pkt_ts = pkt->pts == ffmpeg.AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                pkt_in_play_range = duration == ffmpeg.AV_NOPTS_VALUE ||
                        (pkt_ts - (stream_start_time != ffmpeg.AV_NOPTS_VALUE ? stream_start_time : 0)) *
                        ffmpeg.av_q2d(ic->streams[pkt->stream_index]->time_base) -
                        (double)(start_time != ffmpeg.AV_NOPTS_VALUE ? start_time : 0) / 1000000
                        <= ((double)duration / 1000000);

                if (pkt->stream_index == vst.audio_stream && pkt_in_play_range)
                {
                    packet_queue_put(vst.audioq, pkt);
                }
                else if (pkt->stream_index == vst.video_stream && pkt_in_play_range
                         && (vst.video_st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    packet_queue_put(vst.videoq, pkt);
                }
                else if (pkt->stream_index == vst.subtitle_stream && pkt_in_play_range)
                {
                    packet_queue_put(vst.subtitleq, pkt);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }

            ret = 0;
            fail:
            if (ic != null && vst.ic == null)
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

        private VideoState stream_open(string filename, AVInputFormat* iformat)
        {
            var vst = new VideoState();

            vst.filename = filename;
            vst.iformat = iformat;
            vst.ytop = 0;
            vst.xleft = 0;
            if (frame_queue_init(vst.pictq, vst.videoq, VIDEO_PICTURE_QUEUE_SIZE, true) < 0)
                goto fail;
            if (frame_queue_init(vst.subpq, vst.subtitleq, SUBPICTURE_QUEUE_SIZE, false) < 0)
                goto fail;
            if (frame_queue_init(vst.sampq, vst.audioq, SAMPLE_QUEUE_SIZE, true) < 0)
                goto fail;
            if (packet_queue_init(vst.videoq) < 0 ||
                packet_queue_init(vst.audioq) < 0 ||
                packet_queue_init(vst.subtitleq) < 0)
                goto fail;

            vst.continue_read_thread = SDL_CreateCond();

            init_clock(vst.vidclk, vst.videoq.serial);
            init_clock(vst.audclk, vst.audioq.serial);
            init_clock(vst.extclk, vst.extclk.serial);
            vst.audio_clock_serial = -1;
            vst.audio_volume = SDL_MIX_MAXVOLUME;
            vst.muted = false;
            vst.av_sync_type = av_sync_type;
            vst.read_tid = SDL_CreateThread(read_thread, vst);

            fail:
            stream_close(vst);

            return vst;
        }

        static void seek_chapter(VideoState vst, int incr)
        {
            long pos = Convert.ToInt64(get_master_clock(vst) * ffmpeg.AV_TIME_BASE);
            int i = 0;

            if (vst.ic->nb_chapters == 0)
                return;

            for (i = 0; i < vst.ic->nb_chapters; i++)
            {
                AVChapter* ch = vst.ic->chapters[i];
                if (ffmpeg.av_compare_ts(pos, ffmpeg.AV_TIME_BASE_Q, ch->start, ch->time_base) < 0)
                {
                    i--;
                    break;
                }
            }

            i += incr;
            i = Math.Max(i, 0);
            if (i >= vst.ic->nb_chapters)
                return;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Seeking to chapter {i}.\n");
            stream_seek(vst, ffmpeg.av_rescale_q(vst.ic->chapters[i]->start, vst.ic->chapters[i]->time_base,
                                         ffmpeg.AV_TIME_BASE_Q), 0, false);
        }

        private void stream_cycle_channel(VideoState vst, AVMediaType codec_type)
        {
            var ic = vst.ic;
            int start_index, stream_index;
            int old_index;
            AVStream* st;
            AVProgram* p = null;
            int nb_streams = (int)vst.ic->nb_streams;

            if (codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                start_index = vst.last_video_stream;
                old_index = vst.video_stream;
            }
            else if (codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                start_index = vst.last_audio_stream;
                old_index = vst.audio_stream;
            }
            else
            {
                start_index = vst.last_subtitle_stream;
                old_index = vst.subtitle_stream;
            }
            stream_index = start_index;
            if (codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO && vst.video_stream != -1)
            {
                p = ffmpeg.av_find_program_from_stream(ic, null, vst.video_stream);
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
                st = vst.ic->streams[p != null ? (int)p->stream_index[stream_index] : stream_index];
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


        private void event_loop(VideoState cur_stream)
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
                        cur_stream.force_refresh = true;
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
                        if (cur_stream.ic->nb_chapters <= 1)
                        {
                            incr = 600.0;
                            goto do_seek;
                        }
                        seek_chapter(cur_stream, 1);
                        break;
                    case EventAction.PreviousChapter:
                        if (cur_stream.ic->nb_chapters <= 1)
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
                        if (seek_by_bytes != 0)
                        {
                            pos = -1;
                            if (pos < 0 && cur_stream.video_stream >= 0)
                                pos = frame_queue_last_pos(cur_stream.pictq);

                            if (pos < 0 && cur_stream.audio_stream >= 0)
                                pos = frame_queue_last_pos(cur_stream.sampq);

                            if (pos < 0)
                                pos = cur_stream.ic->pb->pos; // TODO: ffmpeg.avio_tell(cur_stream.ic->pb); avio_tell not available here

                            if (cur_stream.ic->bit_rate != 0)
                                incr *= cur_stream.ic->bit_rate / 8.0;
                            else
                                incr *= 180000.0;

                            pos += incr;
                            stream_seek(cur_stream, (long)pos, (long)incr, Convert.ToBoolean(seek_by_bytes));
                        }
                        else
                        {
                            pos = get_master_clock(cur_stream);
                            if (double.IsNaN(pos))
                                pos = (double)cur_stream.seek_pos / ffmpeg.AV_TIME_BASE;
                            pos += incr;

                            if (cur_stream.ic->start_time != ffmpeg.AV_NOPTS_VALUE && pos < cur_stream.ic->start_time / (double)ffmpeg.AV_TIME_BASE)
                                pos = cur_stream.ic->start_time / (double)ffmpeg.AV_TIME_BASE;

                            stream_seek(cur_stream, (long)(pos * ffmpeg.AV_TIME_BASE), (long)(incr * ffmpeg.AV_TIME_BASE), Convert.ToBoolean(seek_by_bytes));
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