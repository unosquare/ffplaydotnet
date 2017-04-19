namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;

    unsafe partial class FFplay
    {
        public delegate int InterruptCallbackDelegate(void* opaque);
        public delegate int LockManagerCallbackDelegate(void** mutex, AVLockOp op);

        #region Supporting Classes
        public class PacketSequenceNode
        {
            public AVPacket* Packet;
            public PacketSequenceNode Next { get; set; }
            public int Serial { get; set; }
        }

        public class PacketQueue
        {
            static internal readonly AVPacket* FlushPacket = null;
            static PacketQueue()
            {
                ffmpeg.av_init_packet(FlushPacket);
                FlushPacket->data = (byte*)FlushPacket;
            }

            public PacketSequenceNode FirstNode { get; internal set; }
            public PacketSequenceNode LastNode { get; internal set; }

            public int Length { get; internal set; }
            public int ByteLength { get; internal set; }
            public long Duration { get; internal set; }
            public bool IsPendingAbort { get; internal set; }
            public int Serial { get; internal set; }



            public SDL_mutex Mutex;
            public SDL_cond MutexCondition;


            private int EnqueueInternal(AVPacket* packet)
            {
                if (IsPendingAbort)
                    return -1;

                var node = new PacketSequenceNode();
                node.Packet = packet;
                node.Next = null;
                if (packet == PacketQueue.FlushPacket)
                    Serial++;

                node.Serial = Serial;

                if (LastNode == null)
                    FirstNode = node;
                else
                    LastNode.Next = node;

                LastNode = node;
                Length++;
                ByteLength += node.Packet->size; // + sizeof(*pkt1); // TODO: unsure how to do this or if needed
                Duration += node.Packet->duration;

                SDL_CondSignal(MutexCondition);
                return 0;
            }

            internal int Enqueue(AVPacket* packet)
            {
                SDL_LockMutex(Mutex);
                var ret = EnqueueInternal(packet);
                SDL_UnlockMutex(Mutex);
                if (packet != PacketQueue.FlushPacket && ret < 0)
                    ffmpeg.av_packet_unref(packet);
                return ret;
            }

            internal int EnqueueNull(int streamIndex)
            {
                var packet = new AVPacket();
                ffmpeg.av_init_packet(&packet);

                packet.data = null;
                packet.size = 0;
                packet.stream_index = streamIndex;
                return Enqueue(&packet);
            }

            internal int Initialize()
            {
                Mutex = SDL_CreateMutex();
                MutexCondition = SDL_CreateCond();
                IsPendingAbort = true;
                return 0;
            }

            internal void Flush()
            {
                // port of: packet_queue_flush;

                PacketSequenceNode currentNode;
                PacketSequenceNode nextNode;

                SDL_LockMutex(Mutex);

                for (currentNode = FirstNode; currentNode != null; currentNode = nextNode)
                {
                    nextNode = currentNode.Next;
                    ffmpeg.av_packet_unref(currentNode.Packet);
                }

                LastNode = null;
                FirstNode = null;
                Length = 0;
                ByteLength = 0;
                Duration = 0;

                SDL_UnlockMutex(Mutex);
            }

            internal void Destroy()
            {
                Flush();
                SDL_DestroyMutex(Mutex);
                SDL_DestroyCond(MutexCondition);
            }

            internal void Abort()
            {
                SDL_LockMutex(Mutex);
                IsPendingAbort = true;
                SDL_CondSignal(MutexCondition);
                SDL_UnlockMutex(Mutex);
            }

            internal void Start()
            {
                SDL_LockMutex(Mutex);
                IsPendingAbort = false;
                EnqueueInternal(PacketQueue.FlushPacket);
                SDL_UnlockMutex(Mutex);
            }

            internal int Dequeue(AVPacket* packet, bool block, ref int serial)
            {
                PacketSequenceNode node = null;
                int result = 0;

                SDL_LockMutex(Mutex);
                while (true)
                {
                    if (IsPendingAbort)
                    {
                        result = -1;
                        break;
                    }

                    node = FirstNode;
                    if (node != null)
                    {
                        FirstNode = node.Next;
                        if (FirstNode == null)
                            LastNode = null;

                        Length--;
                        ByteLength -= node.Packet->size; // + sizeof(*pkt1); // TODO: Verify
                        Duration -= node.Packet->duration;

                        packet = node.Packet;

                        if (serial != 0)
                            serial = node.Serial;

                        result = 1;
                        break;
                    }
                    else if (block == false)
                    {
                        result = 0;
                        break;
                    }
                    else
                    {
                        SDL_CondWait(MutexCondition, Mutex);
                    }
                }

                SDL_UnlockMutex(Mutex);
                return result;
            }

        }

        public class AudioParams
        {
            public int Frequency { get; internal set; }
            public int ChannelCount { get; internal set; }
            public long ChannelLayout { get; internal set; }
            public AVSampleFormat SampleFormat { get; internal set; }
            public int FrameSize { get; internal set; }
            public int BytesPerSecond { get; internal set; }

            public void CopyTo(AudioParams other)
            {
                other.BytesPerSecond = BytesPerSecond;
                other.ChannelCount = ChannelCount;
                other.ChannelLayout = ChannelLayout;
                other.SampleFormat = SampleFormat;
                other.FrameSize = FrameSize;
                other.Frequency = Frequency;
            }
        }

        public class Clock
        {
            public double pts;           /* clock base */
            public double pts_drift;     /* clock base minus time at which we updated the clock */
            public double last_updated;
            public int serial;           /* clock is based on a packet with this serial */
            public double speed;
            public bool paused;
            public int? queue_serial; /* pointer to the current packet queue serial, used for obsolete clock detection */
        }

        public class Frame
        {
            public AVFrame* DecodedFrame;
            public AVSubtitle Subtitle;
            public int Serial;
            public double PresentationTimestamp;           /* presentation timestamp for the frame */
            public double EstimatedDuration;      /* estimated duration of the frame */
            public long BytePosition;          /* byte position of the frame in the input file */
            public SDL_Texture bmp;
            public bool IsAllocated;
            public int PictureWidth;
            public int PictureHeight;
            public int format;
            public AVRational PictureAspectRatio;
            public bool IsUploaded;
        }

        public class FrameQueue
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

        public class Decoder
        {
            public AVPacket pkt;
            public AVPacket pkt_temp;
            public PacketQueue queue;
            public AVCodecContext* avctx;
            public int pkt_serial;
            public bool finished;
            public bool IsPacketPending;
            public SDL_cond empty_queue_cond;
            public long start_pts;
            public AVRational start_pts_tb;
            public long next_pts;
            public AVRational next_pts_tb;
            public SDL_Thread DecoderThread;
        }

        public class MediaState
        {
            public MediaState()
            {
                Handle = GCHandle.Alloc(this, GCHandleType.Pinned);
            }

            public readonly GCHandle Handle;
            public SDL_Thread ReadThread;
            public AVInputFormat* InputFormat;
            public bool IsAbortRequested { get; internal set; }
            public bool IsForceRefreshRequested { get; internal set; }
            public bool IsPaused { get; set; }

            public int last_paused;
            public bool queue_attachments_req;
            public bool IsSeekRequested;
            public int seek_flags;
            public long seek_pos;
            public long seek_rel;
            public int read_pause_return;

            public AVFormatContext* InputContext;

            public bool IsMediaRealtime { get; internal set; }

            public Clock AudioClock { get; } = new Clock();
            public Clock VideoClock { get; } = new Clock();
            public Clock ExternalClock { get; } = new Clock();

            public FrameQueue PictureQueue { get; } = new FrameQueue();
            public FrameQueue SubtitleQueue { get; } = new FrameQueue();
            public FrameQueue AudioQueue { get; } = new FrameQueue();

            public Decoder AudioDecoder { get; } = new Decoder();
            public Decoder VideoDecoder { get; } = new Decoder();
            public Decoder SubtitleDecoder { get; } = new Decoder();

            public int audio_stream;
            public SyncMode MediaSyncMode { get; set; }
            public SyncMode MasterSyncMode
            {
                get
                {
                    if (MediaSyncMode == SyncMode.AV_SYNC_VIDEO_MASTER)
                    {
                        if (video_st != null)
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
            public double audio_clock;
            public int audio_clock_serial;
            public double audio_diff_cum; /* used for AV difference average computation */
            public double audio_diff_avg_coef;
            public double audio_diff_threshold;
            public int audio_diff_avg_count;
            public AVStream* AudioStream;
            public PacketQueue AudioPackets { get; } = new PacketQueue();
            public int audio_hw_buf_size;
            public byte* audio_buf;
            public byte* audio_buf1;
            public uint audio_buf_size; /* in bytes */
            public uint audio_buf1_size;
            public int audio_buf_index; /* in bytes */
            public int audio_write_buf_size;
            public int AudioVolume { get; set; }
            public bool IsAudioMuted { get; set; }
            public AudioParams AudioInputParams { get; } = new AudioParams();
            public AudioParams AudioOutputParams { get; } = new AudioParams();

            public SwrContext* swr_ctx;

            public int frame_drops_early;
            public int frame_drops_late;

            public short[] sample_array = new short[SAMPLE_ARRAY_SIZE];
            public int sample_array_index;
            public int last_i_start;

            public int xpos;
            public double last_vis_time;
            public SDL_Texture vis_texture;
            public SDL_Texture sub_texture;
            public int SubtitleStreamIndex { get; internal set; }
            public AVStream* subtitle_st;
            public PacketQueue SubtitlePackets { get; } = new PacketQueue();

            public double frame_timer;
            public double frame_last_returned_time;
            public double frame_last_filter_delay;
            public int VideoStreamIndex { get; internal set; }
            public AVStream* video_st;
            public PacketQueue VideoPackets { get; } = new PacketQueue();

            /// <summary>
            /// Gets the maximum duration of the frame.
            /// above this, we consider the jump a timestamp discontinuity
            /// </summary>
            public double MaximumFrameDuration { get; internal set; }

            public SwsContext* img_convert_ctx;
            public SwsContext* sub_convert_ctx;

            public bool IsAtEndOfFile { get; internal set; }
            public string MediaUrl { get; internal set; }
            public int PictureWidth;
            public int height;
            public int xleft;
            public int ytop;
            public bool step;

            public int last_video_stream, last_audio_stream, last_subtitle_stream;

            public SDL_cond continue_read_thread;
        }

        #endregion

    }
}
