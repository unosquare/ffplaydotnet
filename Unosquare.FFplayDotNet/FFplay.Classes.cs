namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;

    unsafe partial class FFplay
    {
        internal delegate int InterruptCallbackDelegate(void* opaque);
        internal delegate int LockManagerCallbackDelegate(void** mutex, AVLockOp op);

        #region Supporting Classes
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
            public int size;
            public long duration;
            public bool abort_request;
            public int serial;
            public SDL_mutex mutex;
            public SDL_cond cond;
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
            public int serial;           /* clock is based on a packet with this serial */
            public double speed;
            public bool paused;
            public int? queue_serial; /* pointer to the current packet queue serial, used for obsolete clock detection */
        }

        internal class Frame
        {
            public AVFrame* frame;
            public AVSubtitle sub;
            public int serial;
            public double pts;           /* presentation timestamp for the frame */
            public double duration;      /* estimated duration of the frame */
            public long pos;          /* byte position of the frame in the input file */
            public SDL_Texture bmp;
            public bool allocated;
            public int width;
            public int height;
            public int format;
            public AVRational sar;
            public bool uploaded;
        }

        internal class FrameQueue
        {
            public Frame[] queue = new Frame[FRAME_QUEUE_SIZE];
            public int rindex;
            public int windex;
            public int size;
            public int max_size;
            public bool keep_last;
            public int rindex_shown;
            public SDL_mutex mutex;
            public SDL_cond cond;
            public PacketQueue pktq;
        }

        internal class Decoder
        {
            public AVPacket pkt;
            public AVPacket pkt_temp;
            public PacketQueue queue;
            public AVCodecContext* avctx;
            public int pkt_serial;
            public bool finished;
            public bool packet_pending;
            public SDL_cond empty_queue_cond;
            public long start_pts;
            public AVRational start_pts_tb;
            public long next_pts;
            public AVRational next_pts_tb;
            public SDL_Thread decoder_tid;
        }

        internal class VideoState
        {
            public VideoState()
            {
                Handle = GCHandle.Alloc(this, GCHandleType.Pinned);
            }

            public readonly GCHandle Handle;
            public SDL_Thread read_tid;
            public AVInputFormat* iformat;
            public bool abort_request;
            public bool force_refresh;
            public bool paused;
            public int last_paused;
            public bool queue_attachments_req;
            public bool seek_req;
            public int seek_flags;
            public long seek_pos;
            public long seek_rel;
            public int read_pause_return;
            public AVFormatContext* ic;
            public bool realtime;
            public Clock audclk;
            public Clock vidclk;
            public Clock extclk;
            public FrameQueue pictq = new FrameQueue();
            public FrameQueue subpq = new FrameQueue();
            public FrameQueue sampq = new FrameQueue();
            public Decoder auddec = new Decoder();
            public Decoder viddec = new Decoder();
            public Decoder subdec = new Decoder(); // subtitle decoder
            public int audio_stream;
            public SyncMode av_sync_type;
            public double audio_clock;
            public int audio_clock_serial;
            public double audio_diff_cum; /* used for AV difference average computation */
            public double audio_diff_avg_coef;
            public double audio_diff_threshold;
            public int audio_diff_avg_count;
            public AVStream* audio_st;
            public PacketQueue audioq = new PacketQueue();
            public int audio_hw_buf_size;
            public byte* audio_buf;
            public byte* audio_buf1;
            public uint audio_buf_size; /* in bytes */
            public uint audio_buf1_size;
            public int audio_buf_index; /* in bytes */
            public int audio_write_buf_size;
            public int audio_volume;
            public bool muted;
            public AudioParams audio_src;
            public AudioParams audio_tgt;
            public SwrContext* swr_ctx;
            public int frame_drops_early;
            public int frame_drops_late;
            //public ShowMode show_mode;
            public short[] sample_array = new short[SAMPLE_ARRAY_SIZE];
            public int sample_array_index;
            public int last_i_start;
            //public RDFTContext rdft;
            //public int rdft_bits;
            //public FFTSample rdft_data;
            public int xpos;
            public double last_vis_time;
            public SDL_Texture vis_texture;
            public SDL_Texture sub_texture;
            public int subtitle_stream;
            public AVStream* subtitle_st;
            public PacketQueue subtitleq = new PacketQueue();
            public double frame_timer;
            public double frame_last_returned_time;
            public double frame_last_filter_delay;
            public int video_stream;
            public AVStream* video_st;
            public PacketQueue videoq = new PacketQueue();
            public double max_frame_duration;      // maximum duration of a frame - above this, we consider the jump a timestamp discontinuity
            public SwsContext* img_convert_ctx;
            public SwsContext* sub_convert_ctx;
            public bool eof;
            public string filename;
            public int width, height, xleft, ytop;
            public bool step;
            public int last_video_stream, last_audio_stream, last_subtitle_stream;
            public SDL_cond continue_read_thread;
        }

        #endregion

    }
}
