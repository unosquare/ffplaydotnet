namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows;
    using System.Windows.Media.Imaging;

    /// <summary>
    /// Ported:
    /// https://github.com/FFmpeg/FFmpeg/blob/release/3.1/ffplay.c
    /// </summary>
    internal unsafe partial class FFmpegMedia
    {

        public enum SyncMode
        {
            AV_SYNC_AUDIO_MASTER, /* default choice */
            AV_SYNC_VIDEO_MASTER,
            AV_SYNC_EXTERNAL_CLOCK, /* synchronize to an external clock */
        };

        #region Options specified by the user

        /* options specified by the user */
        private AVInputFormat* file_iformat;
        private string input_filename;
        private string window_title;
        private int fs_screen_width;
        private int fs_screen_height;
        private int default_width = 640;
        private int default_height = 480;
        private int screen_width = 0;
        private int screen_height = 0;
        private int audio_disable;
        private int video_disable;
        private int subtitle_disable;
        private char[] wanted_stream_spec = new char[(int)AVMediaType.AVMEDIA_TYPE_NB];
        private int seek_by_bytes = -1;
        private int display_disable;
        private int show_status = 1;
        private int av_sync_type = (int)SyncMode.AV_SYNC_AUDIO_MASTER;
        private long start_time = ffmpeg.AV_NOPTS_VALUE;
        private long duration = ffmpeg.AV_NOPTS_VALUE;
        private int fast = 0;
        private int genpts = 0;
        private int lowres = 0;
        private int decoder_reorder_pts = -1;
        private int autoexit;
        private int exit_on_keydown;
        private int exit_on_mousedown;
        private int loop = 1;
        private int framedrop = -1;
        private int infinite_buffer = -1;
        private ShowMode show_mode = ShowMode.SHOW_MODE_NONE;
        private string audio_codec_name;
        private string subtitle_codec_name;
        private string video_codec_name;
        double rdftspeed = 0.02;
        private long cursor_last_shown;
        private int cursor_hidden = 0;
        private int autorotate = 1;

        /* current context */
        private int is_full_screen;
        private long audio_callback_time;

        private AVPacket* flush_pkt;

        // private SDL_Surface* screen; // TDO: we need to replace this.

        // TODO: this replaces https://github.com/FFmpeg/FFmpeg/blob/release/3.1/ffplay.c#L1610 which is a static method variable
        private long last_time;
        #endregion

        #region Constants

        const int FRAME_QUEUE_SIZE = 16;
        const int SAMPLE_ARRAY_SIZE = 8 * 65536;
        const int EXTERNAL_CLOCK_MIN_FRAMES = 2;
        const int EXTERNAL_CLOCK_MAX_FRAMES = 10;

        const float EXTERNAL_CLOCK_SPEED_MIN = 0.900f;
        const float EXTERNAL_CLOCK_SPEED_MAX = 1.010f;
        const float EXTERNAL_CLOCK_SPEED_STEP = 0.001f;

        const int MIN_FRAMES = 25;

        /* no AV sync correction is done if below the minimum AV sync threshold */
        const float AV_SYNC_THRESHOLD_MIN = 0.04f;
        /* AV sync correction is done if above the maximum AV sync threshold */
        const float AV_SYNC_THRESHOLD_MAX = 0.1f;
        /* If a frame duration is longer than this, it will not be duplicated to compensate AV sync */
        const float AV_SYNC_FRAMEDUP_THRESHOLD = 0.1f;
        /* no AV correction is done if too big error */
        const float AV_NOSYNC_THRESHOLD = 10.0f;

        const byte SDL_MIX_MAXVOLUME = 128;

        const int AVERROR_NOTSUPP = -40;

        /* we use about AUDIO_DIFF_AVG_NB A-V differences to make the average */
        const int AUDIO_DIFF_AVG_NB = 20;
        /* maximum audio speed change to get correct sync */
        const int SAMPLE_CORRECTION_PERCENT_MAX = 10;

        #endregion

        public FFmpegMedia()
        {
            var vs = new VideoState();

        }

        #region Structures to Classes

        internal class SDL_Event
        {
        }

        internal class MyAVPacketList
        {
            public AVPacket* pkt;
            public MyAVPacketList next;
            public int serial;
        }

        internal class PacketQueue
        {
            public MyAVPacketList first_pkt;
            public MyAVPacketList last_pkt;
            public int nb_packets;
            //public int size;
            public long duration;
            public int abort_request;
            public int serial;
            public Mutex mutex;
            public AutoResetEvent cond;
            public int size;
        }

        internal class AudioParams
        {
            public int freq;
            public int channels;
            public long channel_layout;
            public AVSampleFormat fmt;
            public int frame_size;
            public int bytes_per_sec;
        }

        internal class Clock
        {
            public double pts;           /* clock base */
            public double pts_drift;     /* clock base minus time at which we updated the clock */
            public double last_updated;
            public double speed;
            public int serial;           /* clock is based on a packet with this serial */
            public int paused;
            public int* queue_serial;    /* pointer to the current packet queue serial, used for obsolete clock detection */
        }

        internal class Frame
        {
            public AVFrame* frame;
            public AVSubtitle sub;
            public int serial = 0;
            public double pts = 0;           /* presentation timestamp for the frame */
            public double duration = 0;      /* estimated duration of the frame */
            public long pos = 0;          /* byte position of the frame in the input file */
            public WriteableBitmap bmp; // TODO: we need to replace this
            public int allocated = 0;
            public int reallocate = 0;
            public int width = 0;
            public int height = 0;
            public AVRational sar = new AVRational();
        }

        internal class FrameQueue
        {
            public Frame[] queue = new Frame[FRAME_QUEUE_SIZE];
            public int rindex;
            public int windex;
            public int size;
            public int max_size;
            public int keep_last;
            public int rindex_shown;
            public Mutex mutex;
            public AutoResetEvent cond;
            public PacketQueue pktq;
        }

        internal enum ShowMode
        {
            SHOW_MODE_NONE = -1, SHOW_MODE_VIDEO = 0, SHOW_MODE_WAVES, SHOW_MODE_RDFT, SHOW_MODE_NB
        }

        internal class Decoder
        {
            public AVPacket pkt;
            public AVPacket pkt_temp;
            public PacketQueue queue;
            public AVCodecContext* avctx;
            public int pkt_serial;
            public int finished;
            public int packet_pending;
            public AutoResetEvent empty_queue_cond;
            public long start_pts;
            public AVRational start_pts_tb = new AVRational();
            public long next_pts;
            public AVRational next_pts_tb;
            public Thread decoder_tid;
        }

        internal class VideoState
        {
            public Thread read_tid = null;
            public AVInputFormat* iformat = null;
            public int abort_request = 0;
            public int force_refresh = 0;
            public int paused = 0;
            public int last_paused;
            public int queue_attachments_req;
            public int seek_req;
            public int seek_flags;
            public long seek_pos;
            public long seek_rel;
            public int read_pause_return;
            public AVFormatContext* ic;
            public int realtime;

            public Clock audclk;
            public Clock vidclk;
            public Clock extclk;

            public FrameQueue pictq;
            public FrameQueue subpq;
            public FrameQueue sampq;

            public Decoder auddec;
            public Decoder viddec;
            public Decoder subdec;

            public int viddec_width;
            public int viddec_height;

            public int audio_stream;

            public SyncMode av_sync_type;

            public double audio_clock;
            public int audio_clock_serial;
            public double audio_diff_cum; /* used for AV difference average computation */
            public double audio_diff_avg_coef;
            public double audio_diff_threshold;
            public int audio_diff_avg_count;
            public AVStream* audio_st;
            public PacketQueue audioq;
            public int audio_hw_buf_size;
            public sbyte* audio_buf;
            public sbyte* audio_buf1;
            public uint audio_buf_size; /* in bytes */
            public uint audio_buf1_size;
            public int audio_buf_index; /* in bytes */
            public int audio_write_buf_size;
            public int audio_volume;
            public int muted;
            public AudioParams audio_src;
            public AudioParams audio_tgt;
            public SwrContext* swr_ctx;
            public int frame_drops_early;
            public int frame_drops_late;


            public ShowMode show_mode;
            public short[] sample_array = new short[SAMPLE_ARRAY_SIZE];
            public int sample_array_index;
            public int last_i_start;
            public int rdft_bits;
            public float[] rdft_data;
            public int xpos;
            public double last_vis_time;

            public int subtitle_stream;
            public AVStream* subtitle_st;
            public PacketQueue subtitleq;

            public double frame_timer;
            public double frame_last_returned_time;
            public double frame_last_filter_delay;
            public int video_stream;
            public AVStream* video_st;
            public PacketQueue videoq;
            public double max_frame_duration;      // maximum duration of a frame - above this, we consider the jump a timestamp discontinuity

            public SwsContext* img_convert_ctx;

            public SwsContext* sub_convert_ctx;
            // public SDL_Rect last_display_rect; // TODO: port this to WPF
            public int eof;

            public string filename;
            public int width, height, xleft, ytop;
            public int step;

            public int last_video_stream;
            public int last_audio_stream;
            public int last_subtitle_stream;

            public AutoResetEvent continue_read_thread;

        }

        #endregion

        #region SDL Mutithreading Wrappers

        private object SDL_GetError()
        {
            throw new NotImplementedException();
        }

        private int SDL_OpenAudio(AudioParams wanted_spec, AudioParams spec)
        {
            throw new NotImplementedException();
        }

        private static void SDL_CondSignal(AutoResetEvent cond)
        {
            cond.Set();
        }

        private static void SDL_LockMutex(Mutex mutex)
        {
            mutex.WaitOne();
        }

        private static void SDL_UnlockMutex(Mutex mutex)
        {
            mutex.ReleaseMutex();
        }

        private static Mutex SDL_CreateMutex()
        {
            return new Mutex();
        }

        private void SDL_PushEvent(SDL_Event evnt)
        {
            throw new NotImplementedException();
        }

        private static AutoResetEvent SDL_CreateCond()
        {
            return new AutoResetEvent(false);
        }

        private static void SDL_CondWait(AutoResetEvent cond, Mutex mutex)
        {
            cond.WaitOne();
            mutex.ReleaseMutex();
        }

        private static void SDL_DestroyMutex(Mutex mutex)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            mutex = null;
        }

        private static void SDL_DestroyCond(AutoResetEvent cond)
        {
            cond.Dispose();
            cond = null;
        }

        private static void SDL_WaitThread(Thread t, ref int status)
        {
            t.Join();
        }

        #endregion

        #region Methods

        private int cmp_audio_fmts(AVSampleFormat fmt1, long channel_count1, AVSampleFormat fmt2, long channel_count2)
        {
            /* If channel count == 1, planar and non-planar formats are the same */
            if (channel_count1 == 1 && channel_count2 == 1)
                return ffmpeg.av_get_packed_sample_fmt(fmt1) != ffmpeg.av_get_packed_sample_fmt(fmt2) ? 1 : 0;
            else
                return channel_count1 != channel_count2 || fmt1 != fmt2 ? 1 : 0;
        }

        private long get_valid_channel_layout(long channel_layout, int channels)
        {
            if (channel_layout > 0 && ffmpeg.av_get_channel_layout_nb_channels(Convert.ToUInt64(channel_layout)) == channels)
                return channel_layout;
            else
                return 0;
        }

        private int packet_queue_put_private(PacketQueue packetQueue, AVPacket* mediaPacket)
        {
            if (packetQueue.abort_request > 0)
                return -1;

            var packetList = new MyAVPacketList();
            packetList.pkt = mediaPacket;
            packetList.next = null;

            if (mediaPacket == flush_pkt)
                packetQueue.serial++;

            packetList.serial = packetQueue.serial;

            if (packetQueue.last_pkt == null)
                packetQueue.first_pkt = packetList;
            else
                packetQueue.last_pkt.next = packetList;

            packetQueue.last_pkt = packetList;
            packetQueue.nb_packets++;
            packetQueue.size += packetList.pkt->size; // + sizeof(*packetList); // TODO: check if we can avoid porting this one.
            packetQueue.duration += packetList.pkt->duration;
            /* XXX: should duplicate packet data in DV case */
            SDL_CondSignal(packetQueue.cond);
            return 0;
        }

        private int packet_queue_put(PacketQueue packetQueue, AVPacket* mediaPacket)
        {
            int ret;

            SDL_LockMutex(packetQueue.mutex);
            ret = packet_queue_put_private(packetQueue, mediaPacket);
            SDL_UnlockMutex(packetQueue.mutex);

            if (mediaPacket != flush_pkt && ret < 0)
                ffmpeg.av_packet_unref(mediaPacket);

            return ret;
        }

        private int packet_queue_put_nullpacket(PacketQueue q, int stream_index)
        {
            AVPacket pkt1 = new AVPacket();

            ffmpeg.av_init_packet(&pkt1);
            pkt1.data = null;
            pkt1.size = 0;
            pkt1.stream_index = stream_index;
            return packet_queue_put(q, &pkt1);
        }

        private int packet_queue_init(out PacketQueue q)
        {
            q = new PacketQueue();
            q.mutex = SDL_CreateMutex();
            q.cond = SDL_CreateCond();
            q.abort_request = 1;
            return 0;
        }

        private void packet_queue_flush(PacketQueue q)
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

        private void packet_queue_destroy(PacketQueue q)
        {
            packet_queue_flush(q);
            SDL_DestroyMutex(q.mutex);
            SDL_DestroyCond(q.cond);
        }

        private void packet_queue_abort(PacketQueue q)
        {
            SDL_LockMutex(q.mutex);
            q.abort_request = 1;
            SDL_CondSignal(q.cond);
            SDL_UnlockMutex(q.mutex);
        }

        private void packet_queue_start(PacketQueue q)
        {
            SDL_LockMutex(q.mutex);
            q.abort_request = 0;
            packet_queue_put_private(q, flush_pkt);
            SDL_UnlockMutex(q.mutex);
        }

        /* return < 0 if aborted, 0 if no packet and > 0 if packet.  */
        private int packet_queue_get(PacketQueue q, AVPacket* pkt, int block, ref int serial)
        {
            MyAVPacketList pkt1 = null;
            int ret = 0;

            SDL_LockMutex(q.mutex);

            for (;;)
            {
                if (q.abort_request != 0)
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
                    q.size -= pkt1.pkt->size; // + sizeof(pkt1); // TODO: Hopefully unnecessary
                    q.duration -= pkt1.pkt->duration;
                    pkt = pkt1.pkt;
                    if (serial != 0)
                        serial = pkt1.serial;
                    // ffmpeg.av_free(pkt1); // unnecessary
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

        private void decoder_init(out Decoder d, AVCodecContext* avctx, PacketQueue queue, AutoResetEvent empty_queue_cond)
        {
            d = new Decoder();
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

                if (d.queue.abort_request != 0)
                    return -1;

                if (d.packet_pending == 0 || d.queue.serial != d.pkt_serial)
                {
                    AVPacket pkt;
                    do
                    {
                        if (d.queue.nb_packets == 0)
                            SDL_CondSignal(d.empty_queue_cond);
                        if (packet_queue_get(d.queue, &pkt, 1, ref d.pkt_serial) < 0)
                            return -1;
                        if (pkt.data == flush_pkt->data)
                        {
                            ffmpeg.avcodec_flush_buffers(d.avctx);
                            d.finished = 0;
                            d.next_pts = d.start_pts;
                            d.next_pts_tb = d.start_pts_tb;
                        }
                    } while (pkt.data == flush_pkt->data || d.queue.serial != d.pkt_serial);
                    fixed (AVPacket* refPacket = &d.pkt)
                    {
                        ffmpeg.av_packet_unref(refPacket);
                    }

                    d.pkt_temp = d.pkt = pkt;
                    d.packet_pending = 1;
                }

                switch (d.avctx->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        fixed (AVPacket* pktTemp = &d.pkt_temp)
                        {
                            ret = ffmpeg.avcodec_decode_video2(d.avctx, frame, &got_frame, pktTemp);
                        }

                        if (got_frame != 0)
                        {
                            if (decoder_reorder_pts == -1)
                            {
                                frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
                            }
                            else if (decoder_reorder_pts != 0)
                            {
                                frame->pts = frame->pkt_pts;
                            }
                            else
                            {
                                frame->pts = frame->pkt_dts;
                            }
                        }
                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        fixed (AVPacket* pktTemp = &d.pkt_temp)
                        {
                            ret = ffmpeg.avcodec_decode_audio4(d.avctx, frame, &got_frame, pktTemp);
                        }
                        if (got_frame != 0)
                        {
                            AVRational tb = new AVRational() { num = 1, den = frame->sample_rate };
                            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(frame->pts, d.avctx->time_base, tb);
                            else if (frame->pkt_pts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(frame->pkt_pts, ffmpeg.av_codec_get_pkt_timebase(d.avctx), tb);
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
                    d.packet_pending = 0;
                }
                else
                {
                    d.pkt_temp.dts = ffmpeg.AV_NOPTS_VALUE;
                    d.pkt_temp.pts = ffmpeg.AV_NOPTS_VALUE;
                    if (d.pkt_temp.data != null)
                    {
                        if (d.avctx->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                            ret = d.pkt_temp.size;
                        d.pkt_temp.data += ret;
                        d.pkt_temp.size -= ret;
                        if (d.pkt_temp.size <= 0)
                            d.packet_pending = 0;
                    }
                    else
                    {
                        if (got_frame == 0)
                        {
                            d.packet_pending = 0;
                            d.finished = d.pkt_serial;
                        }
                    }
                }
            } while (got_frame == 0 && d.finished == 0);

            return got_frame;
        }

        private void decoder_destroy(Decoder d)
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

        private void frame_queue_unref_item(Frame vp)
        {
            ffmpeg.av_frame_unref(vp.frame);
        }

        private int frame_queue_init(out FrameQueue f, PacketQueue pktq, int max_size, int keep_last)
        {
            int i;
            f = new FrameQueue();
            f.mutex = SDL_CreateMutex();
            f.cond = SDL_CreateCond();

            f.pktq = pktq;
            f.max_size = Math.Min(max_size, FRAME_QUEUE_SIZE);
            f.keep_last = keep_last == 0 ? 1 : 0;
            for (i = 0; i < f.max_size; i++)
                f.queue[i].frame = ffmpeg.av_frame_alloc();

            return 0;
        }

        private void frame_queue_destory(FrameQueue f)
        {
            int i;
            for (i = 0; i < f.max_size; i++)
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

        private void frame_queue_signal(FrameQueue f)
        {
            SDL_LockMutex(f.mutex);
            SDL_CondSignal(f.cond);
            SDL_UnlockMutex(f.mutex);
        }

        private Frame frame_queue_peek(FrameQueue f)
        {
            return f.queue[(f.rindex + f.rindex_shown) % f.max_size];
        }

        private Frame frame_queue_peek_next(FrameQueue f)
        {
            return f.queue[(f.rindex + f.rindex_shown + 1) % f.max_size];
        }

        private Frame frame_queue_peek_last(FrameQueue f)
        {
            return f.queue[f.rindex];
        }

        private Frame frame_queue_peek_writable(FrameQueue f)
        {
            /* wait until we have space to put a new frame */
            SDL_LockMutex(f.mutex);
            while (f.size >= f.max_size && f.pktq.abort_request == 0)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);

            if (f.pktq.abort_request != 0)
                return null;

            return f.queue[f.windex];
        }

        private Frame frame_queue_peek_readable(FrameQueue f)
        {
            /* wait until we have a readable a new frame */
            SDL_LockMutex(f.mutex);
            while (f.size - f.rindex_shown <= 0 &&
                   f.pktq.abort_request == 0)
            {
                SDL_CondWait(f.cond, f.mutex);
            }
            SDL_UnlockMutex(f.mutex);

            if (f.pktq.abort_request != 0)
                return null;

            return f.queue[(f.rindex + f.rindex_shown) % f.max_size];
        }

        private void frame_queue_push(FrameQueue f)
        {
            if (++f.windex == f.max_size)
                f.windex = 0;
            SDL_LockMutex(f.mutex);
            f.size++;
            SDL_CondSignal(f.cond);
            SDL_UnlockMutex(f.mutex);
        }

        private void frame_queue_next(FrameQueue f)
        {
            if (f.keep_last != 0 && f.rindex_shown == 0)
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

        /* return the number of undisplayed frames in the queue */
        private int frame_queue_nb_remaining(FrameQueue f)
        {
            return f.size - f.rindex_shown;
        }

        /* return last shown position */
        private long frame_queue_last_pos(FrameQueue f)
        {
            Frame fp = f.queue[f.rindex];
            if (f.rindex_shown != 0 && fp.serial == f.pktq.serial)
                return fp.pos;
            else
                return -1;
        }

        private void decoder_abort(Decoder d, FrameQueue fq)
        {
            packet_queue_abort(d.queue);
            frame_queue_signal(fq);
            var tStatus = 0;
            SDL_WaitThread(d.decoder_tid, ref tStatus);
            d.decoder_tid = null;
            packet_queue_flush(d.queue);
        }

        private void free_picture(Frame vp)
        {
            // TODO: free the BMP
            //if (vp->bmp)
            //{
            //    SDL_FreeYUVOverlay(vp->bmp);
            //    vp->bmp = NULL;
            //}
        }

        private float av_q2d(AVRational rat)
        {
            return (float)rat.num / (float)rat.den;
        }

        private void calculate_display_rect(ref Rect rect,
                                   int scr_xleft, int scr_ytop, int scr_width, int scr_height,
                                   int pic_width, int pic_height, AVRational pic_sar)
        {
            float aspect_ratio;
            int width, height, x, y;

            if (pic_sar.num == 0)
                aspect_ratio = 0;
            else
                aspect_ratio = av_q2d(pic_sar);

            if (aspect_ratio <= 0.0)
                aspect_ratio = 1.0f;
            aspect_ratio *= (float)pic_width / (float)pic_height;

            /* XXX: we suppose the screen has a 1.0 pixel ratio */
            height = scr_height;
            width = (int)Math.Round(height * aspect_ratio, 0) & ~1;
            if (width > scr_width)
            {
                width = scr_width;
                height = (int)Math.Round(width / aspect_ratio) & ~1;
            }
            x = (scr_width - width) / 2;
            y = (scr_height - height) / 2;
            rect.X = scr_xleft + x;
            rect.Y = scr_ytop + y;
            rect.Width = Math.Max(width, 1);
            rect.Height = Math.Max(height, 1);
        }

        private void video_image_display(VideoState vs)
        {
            Frame vp;
            Frame sp;
            int i;

            vp = frame_queue_peek_last(vs.pictq);
            if (vp.bmp != null)
            {
                if (vs.subtitle_st != null)
                {
                    if (frame_queue_nb_remaining(vs.subpq) > 0)
                    {
                        sp = frame_queue_peek(vs.subpq);

                        if (vp.pts >= sp.pts + ((float)sp.sub.start_display_time / 1000))
                        {
                            // TODO: show subtitle here
                            //sp.sub...
                        }
                    }
                }
            }
        }
        private void SDL_CloseAudio()
        {
            // TODO: SDL implementation
        }

        private void stream_component_close(VideoState vs, int stream_index)
        {
            AVFormatContext* ic = vs.ic;
            AVCodecParameters* codecpar;

            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return;
            codecpar = ic->streams[stream_index]->codecpar;

            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    decoder_abort(vs.auddec, vs.sampq);
                    SDL_CloseAudio();
                    decoder_destroy(vs.auddec);
                    fixed (SwrContext** freeContext = &vs.swr_ctx)
                    {
                        ffmpeg.swr_free(freeContext);
                    }

                    ffmpeg.av_freep(vs.audio_buf1);
                    vs.audio_buf1_size = 0;
                    vs.audio_buf = null;

                    //if (vs.rdft)
                    //{
                    //    ffmpeg.av_rdft_end(vs.rdft);
                    //    ffmpeg.av_freep(vs.rdft_data);
                    //    vs.rdft = null;
                    //    vs.rdft_bits = 0;
                    //}
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    decoder_abort(vs.viddec, vs.pictq);
                    decoder_destroy(vs.viddec);
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    decoder_abort(vs.subdec, vs.subpq);
                    decoder_destroy(vs.subdec);
                    break;
                default:
                    break;
            }

            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_ALL;
            switch (codecpar->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    vs.audio_st = null;
                    vs.audio_stream = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vs.video_st = null;
                    vs.video_stream = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vs.subtitle_st = null;
                    vs.subtitle_stream = -1;
                    break;
                default:
                    break;
            }
        }

        private void stream_close(VideoState vs)
        {
            /* XXX: use a special url_shutdown call to abort parse cleanly */
            var threadStatus = 0;
            vs.abort_request = 1;
            SDL_WaitThread(vs.read_tid, ref threadStatus);

            /* close each stream */
            if (vs.audio_stream >= 0)
                stream_component_close(vs, vs.audio_stream);
            if (vs.video_stream >= 0)
                stream_component_close(vs, vs.video_stream);
            if (vs.subtitle_stream >= 0)
                stream_component_close(vs, vs.subtitle_stream);

            fixed (AVFormatContext** inputContext = &vs.ic)
            {
                ffmpeg.avformat_close_input(inputContext);
            }


            packet_queue_destroy(vs.videoq);
            packet_queue_destroy(vs.audioq);
            packet_queue_destroy(vs.subtitleq);

            /* free all pictures */
            frame_queue_destory(vs.pictq);
            frame_queue_destory(vs.sampq);
            frame_queue_destory(vs.subpq);
            SDL_DestroyCond(vs.continue_read_thread);

            ffmpeg.sws_freeContext(vs.img_convert_ctx);
            ffmpeg.sws_freeContext(vs.sub_convert_ctx);

            // TODO: there are some useless av_free stuff not needed in managed code.
        }

        private void set_default_window_size(int width, int height, AVRational sar)
        {
            Rect rect = new Rect();
            calculate_display_rect(ref rect, 0, 0, int.MaxValue, height, width, height, sar);
            default_width = (int)rect.Width;
            default_height = (int)rect.Height;
        }


        private int video_open(VideoState vs, int force_set_video_mode, Frame vp)
        {

            var w = default_width;
            var h = default_height;

            w = Math.Min(16383, w);
            vs.width = w;
            vs.height = h;

            return 0;
        }

        private void video_display(VideoState vs)
        {
            video_image_display(vs);
        }

        private double get_clock(Clock c)
        {
            if (*c.queue_serial != c.serial)
                return double.NaN;
            if (c.paused != 0)
            {
                return c.pts;
            }
            else
            {
                double time = ffmpeg.av_gettime_relative() / 1000000.0;
                return c.pts_drift + time - (time - c.last_updated) * (1.0 - c.speed);
            }
        }

        private void set_clock_at(Clock c, double pts, int serial, double time)
        {
            c.pts = pts;
            c.last_updated = time;
            c.pts_drift = c.pts - time;
            c.serial = serial;
        }

        private void set_clock(Clock c, double pts, int serial)
        {
            double time = ffmpeg.av_gettime_relative() / 1000000.0;
            set_clock_at(c, pts, serial, time);
        }

        private void set_clock_speed(Clock c, double speed)
        {
            set_clock(c, get_clock(c), c.serial);
            c.speed = speed;
        }

        private void init_clock(Clock c, int* queue_serial)
        {
            c.speed = 1.0;
            c.paused = 0;
            c.queue_serial = queue_serial;
            set_clock(c, double.NaN, -1);
        }

        private void sync_clock_to_slave(Clock c, Clock slave)
        {
            double clock = get_clock(c);
            double slave_clock = get_clock(slave);
            if (!double.IsNaN(slave_clock) && (double.IsNaN(clock) || Math.Abs(clock - slave_clock) > AV_NOSYNC_THRESHOLD))
                set_clock(c, slave_clock, slave.serial);
        }

        private SyncMode get_master_sync_type(VideoState vs)
        {
            if (vs.av_sync_type == SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                if (vs.video_st != null)
                    return SyncMode.AV_SYNC_VIDEO_MASTER;
                else
                    return SyncMode.AV_SYNC_AUDIO_MASTER;
            }
            else if (vs.av_sync_type == SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                if (vs.audio_st != null)
                    return SyncMode.AV_SYNC_AUDIO_MASTER;
                else
                    return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
            }
            else
            {
                return SyncMode.AV_SYNC_EXTERNAL_CLOCK;
            }
        }

        /* get the current master clock value */
        private double get_master_clock(VideoState vs)
        {
            double val;

            switch (get_master_sync_type(vs))
            {
                case SyncMode.AV_SYNC_VIDEO_MASTER:
                    val = get_clock(vs.vidclk);
                    break;
                case SyncMode.AV_SYNC_AUDIO_MASTER:
                    val = get_clock(vs.audclk);
                    break;
                default:
                    val = get_clock(vs.extclk);
                    break;
            }
            return val;
        }

        private void check_external_clock_speed(VideoState vs)
        {
            if (vs.video_stream >= 0 && vs.videoq.nb_packets <= EXTERNAL_CLOCK_MIN_FRAMES ||
                vs.audio_stream >= 0 && vs.audioq.nb_packets <= EXTERNAL_CLOCK_MIN_FRAMES)
            {
                set_clock_speed(vs.extclk, Math.Max(EXTERNAL_CLOCK_SPEED_MIN, vs.extclk.speed - EXTERNAL_CLOCK_SPEED_STEP));
            }
            else if ((vs.video_stream < 0 || vs.videoq.nb_packets > EXTERNAL_CLOCK_MAX_FRAMES) &&
                    (vs.audio_stream < 0 || vs.audioq.nb_packets > EXTERNAL_CLOCK_MAX_FRAMES))
            {
                set_clock_speed(vs.extclk, Math.Min(EXTERNAL_CLOCK_SPEED_MAX, vs.extclk.speed + EXTERNAL_CLOCK_SPEED_STEP));
            }
            else
            {
                double speed = vs.extclk.speed;
                if (speed != 1.0)
                    set_clock_speed(vs.extclk, speed + EXTERNAL_CLOCK_SPEED_STEP * (1.0 - speed) / Math.Abs(1.0 - speed));
            }
        }

        private void stream_seek(VideoState vs, long pos, long rel, int seek_by_bytes)
        {
            if (vs.seek_req == 0)
            {
                vs.seek_pos = pos;
                vs.seek_rel = rel;
                vs.seek_flags &= ~ffmpeg.AVSEEK_FLAG_BYTE;
                if (seek_by_bytes != 0)
                    vs.seek_flags |= ffmpeg.AVSEEK_FLAG_BYTE;
                vs.seek_req = 1;
                SDL_CondSignal(vs.continue_read_thread);
            }
        }

        private void stream_toggle_pause(VideoState vs)
        {
            if (vs.paused != 0)
            {
                vs.frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - vs.vidclk.last_updated;
                if (vs.read_pause_return != AVERROR_NOTSUPP)
                {
                    vs.vidclk.paused = 0;
                }
                set_clock(vs.vidclk, get_clock(vs.vidclk), vs.vidclk.serial);
            }
            set_clock(vs.extclk, get_clock(vs.extclk), vs.extclk.serial);
            vs.paused = vs.audclk.paused = vs.vidclk.paused = vs.extclk.paused = (vs.paused == 0) ? 1 : 0;
        }

        private void toggle_pause(VideoState vs)
        {
            stream_toggle_pause(vs);
            vs.step = 0;
        }

        private void toggle_mute(VideoState vs)
        {
            vs.muted = vs.muted == 0 ? 1 : 0;
        }

        private void update_volume(VideoState vs, int sign, int step)
        {
            vs.audio_volume = av_clip(vs.audio_volume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        private int av_clip(int v1, int amin, int amax)
        {
            if (v1 > amax) return amax;
            if (v1 < amin) return amin;

            return v1;
        }

        private void step_to_next_frame(VideoState vs)
        {
            /* if the stream is paused unpause it, then step */
            if (vs.paused != 0)
                stream_toggle_pause(vs);
            vs.step = 1;
        }

        private double compute_target_delay(double delay, VideoState vs)
        {
            double sync_threshold, diff = 0;

            /* update delay to follow master synchronisation source */
            if (get_master_sync_type(vs) != SyncMode.AV_SYNC_VIDEO_MASTER)
            {
                /* if video is slave, we try to correct big delays by
                   duplicating or deleting a frame */
                diff = get_clock(vs.vidclk) - get_master_clock(vs);

                /* skip or repeat frame. We take into account the
                   delay to compute the threshold. I still don't know
                   if it is the best guess */
                sync_threshold = Math.Max(AV_SYNC_THRESHOLD_MIN, Math.Min(AV_SYNC_THRESHOLD_MAX, delay));
                if (!double.IsNaN(diff) && Math.Abs(diff) < vs.max_frame_duration)
                {
                    if (diff <= -sync_threshold)
                        delay = Math.Max(0, delay + diff);
                    else if (diff >= sync_threshold && delay > AV_SYNC_FRAMEDUP_THRESHOLD)
                        delay = delay + diff;
                    else if (diff >= sync_threshold)
                        delay = 2 * delay;
                }
            }

            ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"video: delay={delay} A-V={-diff}\n");

            return delay;
        }

        private double vp_duration(VideoState vs, Frame vp, Frame nextvp)
        {
            if (vp.serial == nextvp.serial)
            {
                double duration = nextvp.pts - vp.pts;
                if (double.IsNaN(duration) || duration <= 0 || duration > vs.max_frame_duration)
                    return vp.duration;
                else
                    return duration;
            }
            else
            {
                return 0.0;
            }
        }

        private void update_video_pts(VideoState vs, double pts, long pos, int serial)
        {
            /* update current video pts */
            set_clock(vs.vidclk, pts, serial);
            sync_clock_to_slave(vs.extclk, vs.vidclk);
        }

        /* called to display each frame */
        private void video_refresh(VideoState vs, ref double remaining_time)
        {
            double time;

            Frame sp;
            Frame sp2;

            if (vs.paused == 0 && get_master_sync_type(vs) == SyncMode.AV_SYNC_EXTERNAL_CLOCK && vs.realtime != 0)
                check_external_clock_speed(vs);

            if (display_disable == 0 && vs.show_mode != ShowMode.SHOW_MODE_VIDEO && vs.audio_st != null)
            {
                time = ffmpeg.av_gettime_relative() / 1000000.0;
                if (vs.force_refresh != 0 || vs.last_vis_time + rdftspeed < time)
                {
                    video_display(vs);
                    vs.last_vis_time = time;
                }

                remaining_time = Math.Min(remaining_time, vs.last_vis_time + rdftspeed - time);
            }

            if (vs.video_st != null)
            {
                retry:
                if (frame_queue_nb_remaining(vs.pictq) == 0)
                {
                    // nothing to do, no picture to display in the queue
                }
                else
                {
                    double last_duration;
                    double duration;
                    double delay;

                    Frame vp;
                    Frame lastvp;

                    /* dequeue the picture */
                    lastvp = frame_queue_peek_last(vs.pictq);
                    vp = frame_queue_peek(vs.pictq);

                    if (vp.serial != vs.videoq.serial)
                    {
                        frame_queue_next(vs.pictq);
                        goto retry;
                    }

                    if (lastvp.serial != vp.serial)
                        vs.frame_timer = ffmpeg.av_gettime_relative() / 1000000.0;

                    if (vs.paused != 0)
                        goto display;

                    /* compute nominal last_duration */
                    last_duration = vp_duration(vs, lastvp, vp);
                    delay = compute_target_delay(last_duration, vs);

                    time = ffmpeg.av_gettime_relative() / 1000000.0;
                    if (time < vs.frame_timer + delay)
                    {
                        remaining_time = Math.Min(vs.frame_timer + delay - time, remaining_time);
                        goto display;
                    }

                    vs.frame_timer += delay;
                    if (delay > 0 && time - vs.frame_timer > AV_SYNC_THRESHOLD_MAX)
                        vs.frame_timer = time;

                    SDL_LockMutex(vs.pictq.mutex);
                    if (!double.IsNaN(vp.pts))
                        update_video_pts(vs, vp.pts, vp.pos, vp.serial);
                    SDL_UnlockMutex(vs.pictq.mutex);

                    if (frame_queue_nb_remaining(vs.pictq) > 1)
                    {
                        Frame nextvp = frame_queue_peek_next(vs.pictq);
                        duration = vp_duration(vs, vp, nextvp);
                        if (vs.step == 0 && (framedrop > 0 || (framedrop != 0 && get_master_sync_type(vs) != SyncMode.AV_SYNC_VIDEO_MASTER)) && time > vs.frame_timer + duration)
                        {
                            vs.frame_drops_late++;
                            frame_queue_next(vs.pictq);
                            goto retry;
                        }
                    }

                    if (vs.subtitle_st != null)
                    {
                        while (frame_queue_nb_remaining(vs.subpq) > 0)
                        {
                            sp = frame_queue_peek(vs.subpq);

                            if (frame_queue_nb_remaining(vs.subpq) > 1)
                                sp2 = frame_queue_peek_next(vs.subpq);
                            else
                                sp2 = null;

                            if (sp.serial != vs.subtitleq.serial
                                || (vs.vidclk.pts > (sp.pts + ((float)sp.sub.end_display_time / 1000)))
                                || (sp2 != null && vs.vidclk.pts > (sp2.pts + ((float)sp2.sub.start_display_time / 1000))))
                            {
                                frame_queue_next(vs.subpq);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    frame_queue_next(vs.pictq);
                    vs.force_refresh = 1;

                    if (vs.step != 0 && vs.paused == 0)
                        stream_toggle_pause(vs);
                }

                display:
                /* display picture */
                if (display_disable == 0 && vs.force_refresh != 0
                    && vs.show_mode == ShowMode.SHOW_MODE_VIDEO
                    && vs.pictq.rindex_shown != 0)
                    video_display(vs);
            }

            vs.force_refresh = 0;
            if (show_status != 0)
            {

                long cur_time;
                int aqsize, vqsize, sqsize;
                double av_diff;

                cur_time = ffmpeg.av_gettime_relative();
                if (last_time == 0 || (cur_time - last_time) >= 30000)
                {
                    aqsize = 0;
                    vqsize = 0;
                    sqsize = 0;
                    if (vs.audio_st != null)
                        aqsize = vs.audioq.size;
                    if (vs.video_st != null)
                        vqsize = vs.videoq.size;
                    if (vs.subtitle_st != null)
                        sqsize = vs.subtitleq.size;

                    av_diff = 0;
                    if (vs.audio_st != null && vs.video_st != null)
                        av_diff = get_clock(vs.audclk) - get_clock(vs.vidclk);
                    else if (vs.video_st != null)
                        av_diff = get_master_clock(vs) - get_clock(vs.vidclk);
                    else if (vs.audio_st != null)
                        av_diff = get_master_clock(vs) - get_clock(vs.audclk);

                    var streamLog = (vs.audio_st != null && vs.video_st != null) ? "A-V" : (vs.video_st != null ? "M-V" : (vs.audio_st != null ? "M-A" : "   "));
                    var faultyDts = vs.video_st != null ? vs.viddec.avctx->pts_correction_num_faulty_dts : 0;
                    var faultyPts = vs.video_st != null ? vs.viddec.avctx->pts_correction_num_faulty_pts : 0;

                    ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO,
                           $"{get_master_clock(vs)} " +
                           $"{streamLog}:{av_diff} fd={vs.frame_drops_early + vs.frame_drops_late} " +
                           $"aq={ aqsize / 1024}KB vq={vqsize / 1024}KB sq={sqsize}B f={faultyDts} / {faultyPts}   \r");

                    last_time = cur_time;
                }
            }
        }

        /* allocate a picture (needs to do that in main thread to avoid
            potential locking problems */
        private void alloc_picture(VideoState vs)
        {
            Frame vp;
            //long bufferdiff;

            vp = vs.pictq.queue[vs.pictq.windex];

            free_picture(vp);

            video_open(vs, 0, vp);

            // TODO: This implementation is missing
            //vp.bmp = new WriteableBitmap();

            SDL_LockMutex(vs.pictq.mutex);
            vp.allocated = 1;
            SDL_CondSignal(vs.pictq.cond);
            SDL_UnlockMutex(vs.pictq.mutex);
        }

        private int queue_picture(VideoState vs, AVFrame* src_frame, double pts, double duration, long pos, int serial)
        {
            Frame vp = frame_queue_peek_writable(vs.pictq);

            if (vp == null)
                return -1;

            vp.sar = src_frame->sample_aspect_ratio;

            /* alloc or resize hardware picture buffer */
            if (vp.bmp == null || vp.reallocate != 0 || vp.allocated == 0 ||
                vp.width != src_frame->width ||
                vp.height != src_frame->height)
            {
                SDL_Event evnt = new SDL_Event();

                vp.allocated = 0;
                vp.reallocate = 0;
                vp.width = src_frame->width;
                vp.height = src_frame->height;

                /* the allocation must be done in the main thread to avoid
                   locking problems. */
                evnt.type = FF_ALLOC_EVENT;
                evnt.user.data1 = vs;
                SDL_PushEvent(evnt);

                /* wait until the picture is allocated */
                SDL_LockMutex(vs.pictq.mutex);
                while (vp.allocated == 0 && vs.videoq.abort_request == 0)
                {
                    SDL_CondWait(vs.pictq.cond, vs.pictq.mutex);
                }
                /* if the queue is aborted, we have to pop the pending ALLOC event or wait for the allocation to complete */
                if (vs.videoq.abort_request != 0 && SDL_PeepEvents(evnt, 1, SDL_GETEVENT, SDL_EVENTMASK(FF_ALLOC_EVENT)) != 1)
                {
                    while (vp.allocated == 0 && vs.abort_request == 0)
                    {
                        SDL_CondWait(vs.pictq.cond, vs.pictq.mutex);
                    }
                }
                SDL_UnlockMutex(vs.pictq.mutex);

                if (vs.videoq.abort_request != 0)
                    return -1;

            }

            if (vp.bmp != null)
            {
                byte[] data = new byte[4];
                byte[] linesize = new byte[4];

                //data[0] = vp.bmp->pixels[0];
                //data[1] = vp.bmp->pixels[2];
                //data[2] = vp.bmp->pixels[1];

                //linesize[0] = vp->bmp->pitches[0];
                //linesize[1] = vp->bmp->pitches[2];
                //linesize[2] = vp->bmp->pitches[1];

                vs.img_convert_ctx = ffmpeg.sws_getCachedContext(vs.img_convert_ctx,
                    vp.width, vp.height, (AVPixelFormat)src_frame->format, vp.width, vp.height,
                    AVPixelFormat.AV_PIX_FMT_BGR24, ffmpeg.SWS_BICUBIC, null, null, null);
                if (vs.img_convert_ctx == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                }

                ffmpeg.sws_scale(vs.img_convert_ctx, &src_frame->data0, src_frame->linesize, 0, vp.height, data, linesize);



                vp.pts = pts;
                vp.duration = duration;
                vp.pos = pos;
                vp.serial = serial;

                /* now we can update the picture count */
                frame_queue_push(vs.pictq);
            }
            return 0;

        }

        private int get_video_frame(VideoState vs, AVFrame* frame)
        {
            int got_picture;

            if ((got_picture = decoder_decode_frame(vs.viddec, frame, null)) < 0)
                return -1;

            if (got_picture != 0)
            {
                double dpts = double.NaN;

                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    dpts = av_q2d(vs.video_st->time_base) * frame->pts;

                frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(vs.ic, vs.video_st, frame);

                vs.viddec_width = frame->width;
                vs.viddec_height = frame->height;

                if (framedrop > 0 || (framedrop != 0 && get_master_sync_type(vs) != SyncMode.AV_SYNC_VIDEO_MASTER))
                {
                    if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double diff = dpts - get_master_clock(vs);
                        if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD &&
                            diff - vs.frame_last_filter_delay < 0 &&
                            vs.viddec.pkt_serial == vs.vidclk.serial &&
                            vs.videoq.nb_packets != 0)
                        {
                            vs.frame_drops_early++;
                            ffmpeg.av_frame_unref(frame);
                            got_picture = 0;
                        }
                    }
                }
            }

            return got_picture;
        }

        private int audio_thread(VideoState vs)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            Frame af;

            int got_frame = 0;
            AVRational tb;
            int ret = 0;

            do
            {
                if ((got_frame = decoder_decode_frame(vs.auddec, frame, null)) < 0)
                    goto the_end;

                if (got_frame != 0)
                {
                    tb = new AVRational { num = 1, den = frame->sample_rate };

                    af = frame_queue_peek_writable(vs.sampq);
                    if (af == null)
                        goto the_end;

                    af.pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * av_q2d(tb);
                    af.pos = ffmpeg.av_frame_get_pkt_pos(frame);
                    af.serial = vs.auddec.pkt_serial;
                    af.duration = av_q2d(new AVRational { num = frame->nb_samples, den = frame->sample_rate });

                    ffmpeg.av_frame_move_ref(af.frame, frame);
                    frame_queue_push(vs.sampq);


                }
            } while (ret >= 0 || ret == AVERROR(EAGAIN) || ret == AVERROR_EOF);
            the_end:
            ffmpeg.av_frame_free(&frame);
            return ret;
        }

        delegate int DecoderStartDelegate(VideoState vs);

        private int decoder_start(Decoder d, DecoderStartDelegate fn, VideoState arg)
        {
            packet_queue_start(d.queue);
            d.decoder_tid = new Thread(() => { fn(arg); }) { IsBackground = true };
            d.decoder_tid.Start();

            return 0;
        }

        private int video_thread(VideoState vs)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            double pts;
            double duration;
            int ret;
            AVRational tb = vs.video_st->time_base;
            AVRational frame_rate = ffmpeg.av_guess_frame_rate(vs.ic, vs.video_st, null);

            for (;;)
            {
                ret = get_video_frame(vs, frame);
                if (ret < 0)
                    break;

                if (ret == 0)
                    continue;

                // TODO: check duration because the rational seems to be mixed.
                duration = (frame_rate.num != 0 && frame_rate.den != 0 ? av_q2d(new AVRational { num = frame_rate.den, den = frame_rate.num }) : 0);
                pts = (frame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : frame->pts * av_q2d(tb);
                ret = queue_picture(vs, frame, pts, duration, ffmpeg.av_frame_get_pkt_pos(frame), vs.viddec.pkt_serial);
                ffmpeg.av_frame_unref(frame);


                if (ret < 0)
                    break;
            }

            ffmpeg.av_frame_free(&frame);
            return 0;
        }

        private int synchronize_audio(VideoState vs, int nb_samples)
        {
            int wanted_nb_samples = nb_samples;

            /* if not master, then we try to remove or add samples to correct the clock */
            if (get_master_sync_type(vs) != SyncMode.AV_SYNC_AUDIO_MASTER)
            {
                double diff;
                double avg_diff;
                int min_nb_samples;
                int max_nb_samples;

                diff = get_clock(vs.audclk) - get_master_clock(vs);

                if (!double.IsNaN(diff) && Math.Abs(diff) < AV_NOSYNC_THRESHOLD)
                {
                    vs.audio_diff_cum = diff + vs.audio_diff_avg_coef * vs.audio_diff_cum;
                    if (vs.audio_diff_avg_count < AUDIO_DIFF_AVG_NB)
                    {
                        /* not enough measures to have a correct estimate */
                        vs.audio_diff_avg_count++;
                    }
                    else
                    {
                        /* estimate the A-V difference */
                        avg_diff = vs.audio_diff_cum * (1.0 - vs.audio_diff_avg_coef);

                        if (Math.Abs(avg_diff) >= vs.audio_diff_threshold)
                        {
                            wanted_nb_samples = nb_samples + (int)(diff * vs.audio_src.freq);
                            min_nb_samples = ((nb_samples * (100 - SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            max_nb_samples = ((nb_samples * (100 + SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            wanted_nb_samples = av_clip(wanted_nb_samples, min_nb_samples, max_nb_samples);
                        }
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wanted_nb_samples - nb_samples} "
                            + $"apts={vs.audio_clock} {vs.audio_diff_threshold}\n");
                    }
                }
                else
                {
                    /* too big difference : may be initial PTS errors, so
                       reset A-V filter */
                    vs.audio_diff_avg_count = 0;
                    vs.audio_diff_cum = 0;
                }
            }

            return wanted_nb_samples;
        }

        private int audio_decode_frame(VideoState vs)
        {
            int data_size;
            int resampled_data_size;
            long dec_channel_layout;
            double audio_clock0;
            int wanted_nb_samples;
            Frame af = null;

            if (vs.paused != 0)
                return -1;

            do
            {
                while (frame_queue_nb_remaining(vs.sampq) == 0)
                {
                    if ((ffmpeg.av_gettime_relative() - audio_callback_time) > 1000000 * vs.audio_hw_buf_size / vs.audio_tgt.bytes_per_sec / 2)
                        return -1;
                    Thread.Sleep(1);
                }

                af = frame_queue_peek_readable(vs.sampq);
                if (af == null)
                    return -1;
                frame_queue_next(vs.sampq);
            } while (af.serial != vs.audioq.serial);

            data_size = ffmpeg.av_samples_get_buffer_size(null, ffmpeg.av_frame_get_channels(af.frame),
                                                   af.frame->nb_samples,
                                                   (AVSampleFormat)af.frame->format, 1);

            dec_channel_layout =
                (af.frame->channel_layout != 0 && ffmpeg.av_frame_get_channels(af.frame) == ffmpeg.av_get_channel_layout_nb_channels(af.frame->channel_layout)) ?
                Convert.ToInt64(af.frame->channel_layout) : ffmpeg.av_get_default_channel_layout(ffmpeg.av_frame_get_channels(af.frame));
            wanted_nb_samples = synchronize_audio(vs, af.frame->nb_samples);

            if (af.frame->format != (int)vs.audio_src.fmt || dec_channel_layout != vs.audio_src.channel_layout || af.frame->sample_rate != vs.audio_src.freq ||
                (wanted_nb_samples != af.frame->nb_samples && vs.swr_ctx == null))
            {
                fixed (SwrContext** contextAddress = &vs.swr_ctx)
                {
                    ffmpeg.swr_free(contextAddress);
                }

                vs.swr_ctx = ffmpeg.swr_alloc_set_opts(null,
                                                 vs.audio_tgt.channel_layout, vs.audio_tgt.fmt, vs.audio_tgt.freq,
                                                 dec_channel_layout, (AVSampleFormat)af.frame->format, af.frame->sample_rate,
                                                 0, null);
                if (vs.swr_ctx == null || ffmpeg.swr_init(vs.swr_ctx) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"Cannot create sample rate converter for conversion of {af.frame->sample_rate} Hz {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)af.frame->format)} {ffmpeg.av_frame_get_channels(af.frame)} channels to {vs.audio_tgt.freq} Hz {ffmpeg.av_get_sample_fmt_name(vs.audio_tgt.fmt)} {vs.audio_tgt.channels} channels!\n");

                    fixed (SwrContext** contextAddress = &vs.swr_ctx)
                    {
                        ffmpeg.swr_free(contextAddress);
                    }
                    return -1;
                }
                vs.audio_src.channel_layout = dec_channel_layout;
                vs.audio_src.channels = ffmpeg.av_frame_get_channels(af.frame);
                vs.audio_src.freq = af.frame->sample_rate;
                vs.audio_src.fmt = (AVSampleFormat)af.frame->format;
            }

            if (vs.swr_ctx != null)
            {
                sbyte** inBuff = af.frame->extended_data;
                sbyte** outBuff = null;
                fixed (sbyte** outBuffAddress = &vs.audio_buf1)
                {
                    outBuff = outBuffAddress;
                }

                int out_count = wanted_nb_samples * vs.audio_tgt.freq / af.frame->sample_rate + 256;
                int out_size = ffmpeg.av_samples_get_buffer_size(null, vs.audio_tgt.channels, out_count, vs.audio_tgt.fmt, 0);
                int len2;
                if (out_size < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size() failed\n");
                    return -1;
                }
                if (wanted_nb_samples != af.frame->nb_samples)
                {
                    if (ffmpeg.swr_set_compensation(vs.swr_ctx, (wanted_nb_samples - af.frame->nb_samples) * vs.audio_tgt.freq / af.frame->sample_rate,
                                                wanted_nb_samples * vs.audio_tgt.freq / af.frame->sample_rate) < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_set_compensation() failed\n");
                        return -1;
                    }
                }

                fixed (sbyte** audioBuf1 = &vs.audio_buf1)
                {
                    fixed (uint* aufioBuf1Size = &vs.audio_buf1_size)
                    {
                        ffmpeg.av_fast_malloc(audioBuf1, aufioBuf1Size, Convert.ToUInt64(out_size));
                    }
                }


                len2 = ffmpeg.swr_convert(vs.swr_ctx, outBuff, out_count, inBuff, af.frame->nb_samples);
                if (len2 < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_convert() failed\n");
                    return -1;
                }
                if (len2 == out_count)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "audio buffer is probably too small\n");
                    if (ffmpeg.swr_init(vs.swr_ctx) < 0)
                    {
                        fixed (SwrContext** contextAddress = &vs.swr_ctx)
                        {
                            ffmpeg.swr_free(contextAddress);
                        }
                    }

                }
                vs.audio_buf = vs.audio_buf1;
                resampled_data_size = len2 * vs.audio_tgt.channels * ffmpeg.av_get_bytes_per_sample(vs.audio_tgt.fmt);
            }
            else
            {
                vs.audio_buf = af.frame->data0;
                resampled_data_size = data_size;
            }

            audio_clock0 = vs.audio_clock;
            /* update the audio clock with the pts */
            if (!double.IsNaN(af.pts))
                vs.audio_clock = af.pts + (double)af.frame->nb_samples / af.frame->sample_rate;
            else
                vs.audio_clock = double.NaN;

            vs.audio_clock_serial = af.serial;

            return resampled_data_size;
        }


        private int audio_open(VideoState vs, long wanted_channel_layout, int wanted_nb_channels, int wanted_sample_rate, AudioParams audio_hw_params)
        {
            AudioParams wanted_spec = new AudioParams();
            AudioParams spec = new AudioParams();

            int[] next_nb_channels = new int[] { 0, 0, 1, 6, 2, 6, 4, 6 };
            int[] next_sample_rates = new int[] { 0, 44100, 48000, 96000, 192000 };
            int next_sample_rate_idx = next_sample_rates.Length - 1;


            wanted_nb_channels = 2; // TODO: hard-coded to 2
            wanted_channel_layout = ffmpeg.av_get_default_channel_layout(wanted_nb_channels);

            if (wanted_channel_layout == 0 || wanted_nb_channels != ffmpeg.av_get_channel_layout_nb_channels(Convert.ToUInt64(wanted_channel_layout)))
            {
                wanted_channel_layout = ffmpeg.av_get_default_channel_layout(wanted_nb_channels);
                wanted_channel_layout &= ~ffmpeg.AV_CH_LAYOUT_STEREO_DOWNMIX;
            }
            wanted_nb_channels = ffmpeg.av_get_channel_layout_nb_channels(Convert.ToUInt64(wanted_channel_layout));
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
            wanted_spec.samples = Math.Max(SDL_AUDIO_MIN_BUFFER_SIZE, 2 << ffmpeg.av_log2(wanted_spec.freq / SDL_AUDIO_MAX_CALLBACKS_PER_SEC));
            wanted_spec.callback = sdl_audio_callback;
            wanted_spec.userdata = vs;

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
            if (spec.format != ffmpeg.AUDIO_S16SYS)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                       "SDL advised audio format %d is not supported!\n", spec.format);
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

        private int stream_component_open(VideoState vs, int stream_index)
        {
            AVFormatContext* ic = vs.ic;
            AVCodecContext* avctx;
            AVCodec* codec;
            string forced_codec_name = null;
            AVDictionary* opts = null;
            AVDictionaryEntry* t = null;
            int sample_rate, nb_channels;
            long channel_layout;
            int ret = 0;
            int stream_lowres = lowres;

            if (stream_index < 0 || stream_index >= ic->nb_streams)
                return -1;

            avctx = ffmpeg.avcodec_alloc_context3(null);
            ret = ffmpeg.avcodec_parameters_to_context(avctx, ic->streams[stream_index]->codecpar);
            if (ret < 0)
                goto fail;

            ffmpeg.av_codec_set_pkt_timebase(avctx, ic->streams[stream_index]->time_base);

            codec = ffmpeg.avcodec_find_decoder(avctx->codec_id);

            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO: vs.last_audio_stream = stream_index; forced_codec_name = audio_codec_name; break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: vs.last_subtitle_stream = stream_index; forced_codec_name = subtitle_codec_name; break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO: vs.last_video_stream = stream_index; forced_codec_name = video_codec_name; break;
            }
            if (string.IsNullOrWhiteSpace(forced_codec_name) == false)
                codec = ffmpeg.avcodec_find_decoder_by_name(forced_codec_name);

            if (codec == null)
            {
                if (string.IsNullOrWhiteSpace(forced_codec_name) == false)
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "No codec could be found with name '%s'\n", forced_codec_name);
                else
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "No codec could be found with id %d\n", avctx->codec_id);

                ret = AVERROR(EINVAL);
                goto fail;
            }

            avctx->codec_id = codec->id;
            if (stream_lowres > ffmpeg.av_codec_get_max_lowres(codec))
            {
                ffmpeg.av_log(avctx, ffmpeg.AV_LOG_WARNING, $"The maximum value for lowres supported by the decoder is {ffmpeg.av_codec_get_max_lowres(codec)}\n");
                stream_lowres = ffmpeg.av_codec_get_max_lowres(codec);
            }
            ffmpeg.av_codec_set_lowres(avctx, stream_lowres);


            if (stream_lowres != 0) avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            if (fast != 0)
                avctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;


            opts = filter_codec_opts(codec_opts, avctx->codec_id, ic, ic->streams[stream_index], codec);
            //if (!av_dict_get(opts, "threads", NULL, 0))
            ffmpeg.av_dict_set(&opts, "threads", "auto", 0);
            if (stream_lowres == 0)
                ffmpeg.av_dict_set_int(&opts, "lowres", stream_lowres, 0);
            if (avctx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || avctx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                ffmpeg.av_dict_set(&opts, "refcounted_frames", "1", 0);
            if ((ret = ffmpeg.avcodec_open2(avctx, codec, &opts)) < 0)
            {
                goto fail;
            }

            t = ffmpeg.av_dict_get(opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX);
            if (t == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Option %s not found.\n", t->key);
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            vs.eof = 0;
            ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_DEFAULT;
            switch (avctx->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:

                    sample_rate = avctx->sample_rate;
                    nb_channels = avctx->channels;
                    channel_layout = Convert.ToInt64(avctx->channel_layout);


                    /* prepare audio output */
                    if ((ret = audio_open(vs, channel_layout, nb_channels, sample_rate, vs.audio_tgt)) < 0)
                        goto fail;
                    vs.audio_hw_buf_size = ret;
                    vs.audio_src = vs.audio_tgt;
                    vs.audio_buf_size = 0;
                    vs.audio_buf_index = 0;

                    /* init averaging filter */
                    vs.audio_diff_avg_coef = exp(log(0.01) / AUDIO_DIFF_AVG_NB);
                    vs.audio_diff_avg_count = 0;
                    /* since we do not have a precise anough audio FIFO fullness,
                       we correct audio sync only if larger than this threshold */
                    vs.audio_diff_threshold = (double)(vs.audio_hw_buf_size) / vs.audio_tgt.bytes_per_sec;

                    vs.audio_stream = stream_index;
                    vs.audio_st = ic->streams[stream_index];

                    decoder_init(out vs.auddec, avctx, vs.audioq, vs.continue_read_thread);
                    if ((vs.ic->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0
                        && vs.ic->iformat->read_seek == IntPtr.Zero)
                    {
                        vs.auddec.start_pts = vs.audio_st->start_time;
                        vs.auddec.start_pts_tb = vs.audio_st->time_base;
                    }
                    if ((ret = decoder_start(vs.auddec, audio_thread, vs)) < 0)
                        goto outfree;
                    SDL_PauseAudio(0);
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    vs.video_stream = stream_index;
                    vs.video_st = ic->streams[stream_index];

                    vs.viddec_width = avctx->width;
                    vs.viddec_height = avctx->height;

                    decoder_init(out vs.viddec, avctx, vs.videoq, vs.continue_read_thread);
                    if ((ret = decoder_start(vs.viddec, video_thread, vs)) < 0)
                        goto outfree;
                    vs.queue_attachments_req = 1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    vs.subtitle_stream = stream_index;
                    vs.subtitle_st = ic->streams[stream_index];

                    decoder_init(out vs.subdec, avctx, vs.subtitleq, vs.continue_read_thread);
                    if ((ret = decoder_start(vs.subdec, subtitle_thread, vs)) < 0)
                        goto outfree;
                    break;
                default:
                    break;
            }
            goto outfree;

            fail:
            ffmpeg.avcodec_free_context(&avctx);
            outfree:
            ffmpeg.av_dict_free(&opts);

            return ret;
        }

        private delegate int DecodeInterruptCallbackDelegate(VideoState vs);

        static int decode_interrupt_cb(VideoState vs)
        {
            return vs.abort_request;
        }

        private int stream_has_enough_packets(AVStream* st, int stream_id, PacketQueue queue)
        {
            if (stream_id < 0 ||
                   queue.abort_request != 0 ||
                   (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 ||
                   queue.nb_packets > MIN_FRAMES && (queue.duration == 0 || av_q2d(st->time_base) * queue.duration > 1.0))
            {
                return 1;
            }

            return 0;
        }

        private int is_realtime(AVFormatContext* s)
        {
            var formatName = Marshal.PtrToStringAnsi(new IntPtr(s->iformat->name));
            var fileName = Marshal.PtrToStringAnsi(new IntPtr(s->filename));

            if (formatName.Equals("rtp") || formatName.Equals("rtsp") || formatName.Equals("sdp"))
                return 1;

            if (s->pb != null && (fileName.StartsWith("rtp:") || fileName.StartsWith("udp:")))
                return 1;

            return 0;
        }

        private int read_thread(VideoState vs)
        {
            AVFormatContext* ic = null;
            int err; int i; int ret;
            int[] st_index = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            AVPacket pkt1; AVPacket* pkt = &pkt1;
            long stream_start_time;
            int pkt_in_play_range = 0;
            AVDictionaryEntry* t;
            AVDictionary** opts;
            int orig_nb_streams;
            Mutex wait_mutex = SDL_CreateMutex();
            int scan_all_pmts_set = 0;
            long pkt_ts;

            for (var si = 0; si < st_index.Length; si++)
                st_index[si] = -1;

            vs.last_video_stream = vs.video_stream = -1;
            vs.last_audio_stream = vs.audio_stream = -1;
            vs.last_subtitle_stream = vs.subtitle_stream = -1;
            vs.eof = 0;

            ic = ffmpeg.avformat_alloc_context();

            ic->interrupt_callback.callback = Marshal.GetFunctionPointerForDelegate(new DecodeInterruptCallbackDelegate(decode_interrupt_cb));
            var vsHandle = GCHandle.Alloc(vs, GCHandleType.Weak);


            ic->interrupt_callback.opaque = (void*)(IntPtr)vsHandle;


            if (ffmpeg.av_dict_get(format_opts, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE) == null)
            {
                ffmpeg.av_dict_set(&format_opts, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
                scan_all_pmts_set = 1;
            }

            err = ffmpeg.avformat_open_input(&ic, vs.filename, vs.iformat, &format_opts);
            if (err < 0)
            {
                print_error(vs.filename, err);
                ret = -1;
                goto fail;
            }
            if (scan_all_pmts_set != 0)
                ffmpeg.av_dict_set(&format_opts, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE);

            if ((t = ffmpeg.av_dict_get(format_opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX)))
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {t->key} not found.\n");
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            vs.ic = ic;

            if (genpts != 0)
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
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING,
                       $"{vs.filename}: could not find codec parameters\n");
                ret = -1;
                goto fail;
            }

            if (ic->pb != null)
                ic->pb->eof_reached = 0; // FIXME hack, ffplay maybe should not use avio_feof() to test for the end

            if (seek_by_bytes < 0)
                seek_by_bytes = !!(ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) && strcmp("ogg", ic->iformat->name);

            vs.max_frame_duration = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) ? 10.0 : 3600.0;

            if (string.IsNullOrWhiteSpace(window_title) && (t = ffmpeg.av_dict_get(ic->metadata, "title", null, 0)))
                window_title = $"{t->value} - {input_filename}";

            /* if seeking requested, we execute it */
            if (start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                long timestamp;

                timestamp = start_time;
                /* add the stream start time */
                if (ic->start_time != ffmpeg.AV_NOPTS_VALUE)
                    timestamp += ic->start_time;
                ret = ffmpeg.avformat_seek_file(ic, -1, long.MinValue, timestamp, long.MaxValue, 0);
                if (ret < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{vs.filename}: could not seek to position {(double)timestamp / ffmpeg.AV_TIME_BASE}\n");
                }
            }

            vs.realtime = is_realtime(ic);

            if (show_status != 0)
            {
                ffmpeg.av_dump_format(ic, 0, vs.filename, 0);
            }


            for (i = 0; i < ic->nb_streams; i++)
            {
                AVStream* st = ic->streams[i];
                AVMediaType type = st->codecpar->codec_type;
                st->discard = AVDiscard.AVDISCARD_ALL;
                if (type >= 0 && wanted_stream_spec[type] && st_index[type] == -1)
                    if (ffmpeg.avformat_match_stream_specifier(ic, st, wanted_stream_spec[(int)type]) > 0)
                        st_index[(int)type] = i;
            }
            for (i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
            {
                if (wanted_stream_spec[i] != 0 && st_index[i] == -1)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Stream specifier %s does not match any %s stream\n", wanted_stream_spec[i], av_get_media_type_string(i));
                    st_index[i] = INT_MAX;
                }
            }

            if (video_disable == 0)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);
            if (audio_disable == 0)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        NULL, 0);
            if (video_disable == 0 && subtitle_disable == 0)
                st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                    ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                        (st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                         st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                        null, 0);

            vs.show_mode = show_mode;
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                AVStream* st = ic->streams[st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]];
                AVCodecParameters* codecpar = st->codecpar;
                AVRational sar = ffmpeg.av_guess_sample_aspect_ratio(ic, st, null);
                if (codecpar->width != 0)
                    set_default_window_size(codecpar->width, codecpar->height, sar);
            }

            /* open the streams */
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
            {
                stream_component_open(vs, st_index[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]);
            }

            ret = -1;
            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                ret = stream_component_open(vs, st_index[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]);
            }
            if (vs.show_mode == ShowMode.SHOW_MODE_NONE)
                vs.show_mode = ret >= 0 ? ShowMode.SHOW_MODE_VIDEO : ShowMode.SHOW_MODE_RDFT;

            if (st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
            {
                stream_component_open(vs, st_index[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE]);
            }

            if (vs.video_stream < 0 && vs.audio_stream < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Failed to open file {vs.filename} or configure filtergraph\n");
                ret = -1;
                goto fail;
            }

            if (infinite_buffer < 0 && vs.realtime != 0)
                infinite_buffer = 1;

            for (;;)
            {
                if (vs.abort_request != 0)
                    break;
                if (vs.paused != vs.last_paused)
                {
                    vs.last_paused = vs.paused;
                    if (vs.paused != 0)
                        vs.read_pause_return = ffmpeg.av_read_pause(ic);
                    else
                        ffmpeg.av_read_play(ic);
                }

                if (vs.paused &&
                        (!strcmp(ic->iformat->name, "rtsp") ||
                         (ic->pb && !strncmp(input_filename, "mmsh:", 5))))
                {
                    /* wait 10 ms to avoid trying to get another packet */
                    /* XXX: horrible */
                    SDL_Delay(10);
                    continue;
                }

                if (vs.seek_req != 0)
                {
                    long seek_target = vs.seek_pos;
                    long seek_min = vs.seek_rel > 0 ? seek_target - vs.seek_rel + 2 : long.MinValue;
                    long seek_max = vs.seek_rel < 0 ? seek_target - vs.seek_rel - 2 : long.MaxValue;
                    // FIXME the +-2 is due to rounding being not done in the correct direction in generation
                    //      of the seek_pos/seek_rel variables

                    ret = ffmpeg.avformat_seek_file(vs.ic, -1, seek_min, seek_target, seek_max, vs.seek_flags);
                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               $"{vs.ic->filename}: error while seeking\n");
                    }
                    else
                    {
                        if (vs.audio_stream >= 0)
                        {
                            packet_queue_flush(vs.audioq);
                            packet_queue_put(vs.audioq, flush_pkt);
                        }
                        if (vs.subtitle_stream >= 0)
                        {
                            packet_queue_flush(vs.subtitleq);
                            packet_queue_put(vs.subtitleq, flush_pkt);
                        }
                        if (vs.video_stream >= 0)
                        {
                            packet_queue_flush(vs.videoq);
                            packet_queue_put(vs.videoq, flush_pkt);
                        }
                        if (vs.seek_flags & ffmpeg.AVSEEK_FLAG_BYTE)
                        {
                            set_clock(vs.extclk, double.NaN, 0);
                        }
                        else
                        {
                            set_clock(vs.extclk, seek_target / (double)ffmpeg.AV_TIME_BASE, 0);
                        }
                    }
                    vs.seek_req = 0;
                    vs.queue_attachments_req = 1;
                    vs.eof = 0;
                    if (vs.paused != 0)
                        step_to_next_frame(vs);
                }

                if (vs.queue_attachments_req != 0)
                {
                    if (vs.video_st != null && (vs.video_st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        AVPacket copy;
                        if ((ret = ffmpeg.av_copy_packet(&copy, &vs.video_st->attached_pic)) < 0)
                            goto fail;

                        packet_queue_put(vs.videoq, &copy);
                        packet_queue_put_nullpacket(vs.videoq, vs.video_stream);
                    }

                    vs.queue_attachments_req = 0;
                }

                /* if the queue are full, no need to read more */
                if (infinite_buffer < 1 &&
                      (vs.audioq.size + vs.videoq.size + vs.subtitleq.size > MAX_QUEUE_SIZE
                    || (stream_has_enough_packets(vs.audio_st, vs.audio_stream, vs.audioq) != 0 &&
                        stream_has_enough_packets(vs.video_st, vs.video_stream, vs.videoq) != 0 &&
                        stream_has_enough_packets(vs.subtitle_st, vs.subtitle_stream, vs.subtitleq) != 0)))
                {
                    /* wait 10 ms */
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vs.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                if (vs.paused == 0 &&
                    (vs.audio_st == null || (vs.auddec.finished == vs.audioq.serial && frame_queue_nb_remaining(vs.sampq) == 0)) &&
                    (vs.video_st == null || (vs.viddec.finished == vs.videoq.serial && frame_queue_nb_remaining(vs.pictq) == 0)))
                {
                    if (loop != 1 && (loop == 0 || --loop != 0))
                    {
                        stream_seek(vs, start_time != ffmpeg.AV_NOPTS_VALUE ? start_time : 0, 0, 0);
                    }
                    else if (autoexit != 0)
                    {
                        ret = ffmpeg.AVERROR_EOF;
                        goto fail;
                    }
                }
                ret = ffmpeg.av_read_frame(ic, pkt);
                if (ret < 0)
                {
                    if ((ret == AVERROR_EOF || ffmpeg.avio_feof(ic->pb)) && vs.eof == 0)
                    {
                        if (vs.video_stream >= 0)
                            packet_queue_put_nullpacket(vs.videoq, vs.video_stream);
                        if (vs.audio_stream >= 0)
                            packet_queue_put_nullpacket(vs.audioq, vs.audio_stream);
                        if (vs.subtitle_stream >= 0)
                            packet_queue_put_nullpacket(vs.subtitleq, vs.subtitle_stream);
                        vs.eof = 1;
                    }
                    if (ic->pb != null && ic->pb->error != 0)
                        break;

                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(vs.continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                else
                {
                    vs.eof = 0;
                }
                /* check if packet is in play range specified by user, then queue, otherwise discard */
                stream_start_time = ic->streams[pkt->stream_index]->start_time;
                pkt_ts = pkt->pts == ffmpeg.AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                pkt_in_play_range = duration == ffmpeg.AV_NOPTS_VALUE ||
                        (pkt_ts - (stream_start_time != ffmpeg.AV_NOPTS_VALUE ? stream_start_time : 0)) * av_q2d(ic->streams[pkt->stream_index]->time_base) -
                        (double)(start_time != ffmpeg.AV_NOPTS_VALUE ? start_time : 0) / 1000000d
                        <= ((double)duration / 1000000d);

                if (pkt->stream_index == vs.audio_stream && pkt_in_play_range)
                {
                    packet_queue_put(&vs.audioq, pkt);
                }
                else if (pkt->stream_index == vs.video_stream && pkt_in_play_range
                         && !(vs.video_st->disposition & AV_DISPOSITION_ATTACHED_PIC))
                {
                    packet_queue_put(vs.videoq, pkt);
                }
                else if (pkt->stream_index == vs.subtitle_stream && pkt_in_play_range)
                {
                    packet_queue_put(vs.subtitleq, pkt);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }

            ret = 0;
            fail:
            if (ic != null && vs.ic == null)
                ffmpeg.avformat_close_input(&ic);

            if (ret != 0)
            {
                SDL_Event ev;

                ev.type = FF_QUIT_EVENT;
                ev.user.data1 = vs;
                SDL_PushEvent(&ev);
            }
            SDL_DestroyMutex(wait_mutex);
            return 0;
        }

        #endregion
    }


}
