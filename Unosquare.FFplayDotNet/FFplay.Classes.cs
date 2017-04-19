namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;

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
            internal static readonly AVPacket* FlushPacket = null;

            static PacketQueue()
            {
                ffmpeg.av_init_packet(FlushPacket);
                FlushPacket->data = (byte*)FlushPacket;
            }

            public PacketSequenceNode FirstNode { get; private set; }
            public PacketSequenceNode LastNode { get; private set; }

            public int Length { get; private set; }
            public int ByteLength { get; private set; }
            public long Duration { get; private set; }
            public bool IsAborted { get; private set; }
            public int Serial { get; private set; }

            private readonly object SyncRoot = new object();

            public PacketQueue()
            {
                lock (SyncRoot)
                {
                    IsAborted = false;
                    EnqueueInternal(PacketQueue.FlushPacket);
                }
            }

            private int EnqueueInternal(AVPacket* packet)
            {
                lock (SyncRoot)
                {
                    if (IsAborted)
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

                    return 0;
                }
            }

            public int Enqueue(AVPacket* packet)
            {
                var result = 0;
                result = EnqueueInternal(packet);

                if (packet != PacketQueue.FlushPacket && result < 0)
                    ffmpeg.av_packet_unref(packet);
                return result;
            }

            public int EnqueueNull(int streamIndex)
            {
                var packet = new AVPacket();
                ffmpeg.av_init_packet(&packet);
                packet.data = null;
                packet.size = 0;
                packet.stream_index = streamIndex;
                return Enqueue(&packet);
            }

            public void Clear()
            {
                // port of: packet_queue_flush;

                PacketSequenceNode currentNode;
                PacketSequenceNode nextNode;

                lock (SyncRoot)
                {
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
                }
            }

            public int Dequeue(AVPacket* packet, ref int serial)
            {
                PacketSequenceNode node = null;
                int result = default(int);

                lock (SyncRoot)
                {
                    while (true)
                    {
                        if (IsAborted)
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
                    }
                }

                return result;
            }

            public void Abort()
            {
                lock (SyncRoot)
                {
                    IsAborted = true;
                }
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
            public Frame[] Frames { get; } = new Frame[FRAME_QUEUE_SIZE];
            public PacketQueue pktq;
            public int rindex;
            public int windex;
            public int size;
            public int max_size;
            public bool keep_last;
            public int rindex_shown;
            public SDL_mutex mutex;
            public SDL_cond cond;

            private void frame_queue_unref_item(Frame vp)
            {
                ffmpeg.av_frame_unref(vp.DecodedFrame);
                fixed (AVSubtitle* vpsub = &vp.Subtitle)
                {
                    ffmpeg.avsubtitle_free(vpsub);
                }


            }

            public int frame_queue_init(PacketQueue queue, int maxSize, bool keepLast)
            {
                mutex = SDL_CreateMutex();
                cond = SDL_CreateCond();

                pktq = queue;
                max_size = Math.Min(maxSize, FRAME_QUEUE_SIZE);
                keep_last = keepLast;
                for (var i = 0; i < max_size; i++)
                    Frames[i].DecodedFrame = ffmpeg.av_frame_alloc();

                return 0;
            }

            public void frame_queue_destory()
            {
                for (var i = 0; i < max_size; i++)
                {
                    var vp = Frames[i];
                    frame_queue_unref_item(vp);
                    fixed (AVFrame** frameRef = &vp.DecodedFrame)
                    {
                        ffmpeg.av_frame_free(frameRef);
                    }

                    free_picture(vp);
                }
                SDL_DestroyMutex(mutex);
                SDL_DestroyCond(cond);
            }

            public void frame_queue_signal()
            {
                SDL_LockMutex(mutex);
                SDL_CondSignal(cond);
                SDL_UnlockMutex(mutex);
            }

            public Frame frame_queue_peek()
            {
                return Frames[(rindex + rindex_shown) % max_size];
            }

            public Frame frame_queue_peek_next()
            {
                return Frames[(rindex + rindex_shown + 1) % max_size];
            }

            public Frame frame_queue_peek_last()
            {
                return Frames[rindex];
            }

            public Frame frame_queue_peek_writable()
            {
                SDL_LockMutex(mutex);

                while (size >= max_size && !pktq.IsAborted)
                {
                    SDL_CondWait(cond, mutex);
                }

                SDL_UnlockMutex(mutex);
                if (pktq.IsAborted)
                    return null;

                return Frames[windex];
            }

            public Frame frame_queue_peek_readable()
            {
                SDL_LockMutex(mutex);
                while (size - rindex_shown <= 0 &&!pktq.IsAborted)
                {
                    SDL_CondWait(cond, mutex);
                }
                SDL_UnlockMutex(mutex);
                if (pktq.IsAborted)
                    return null;
                return Frames[(rindex + rindex_shown) % max_size];
            }

            public void frame_queue_push()
            {
                if (++windex == max_size)
                    windex = 0;
                SDL_LockMutex(mutex);
                size++;
                SDL_CondSignal(cond);
                SDL_UnlockMutex(mutex);
            }

            public void frame_queue_next()
            {
                if (keep_last && !Convert.ToBoolean(rindex_shown))
                {
                    rindex_shown = 1;
                    return;
                }
                frame_queue_unref_item(Frames[rindex]);
                if (++rindex == max_size)
                    rindex = 0;
                SDL_LockMutex(mutex);
                size--;
                SDL_CondSignal(cond);
                SDL_UnlockMutex(mutex);
            }

            public int frame_queue_nb_remaining()
            {
                return size - rindex_shown;
            }

            public long frame_queue_last_pos()
            {
                var fp = Frames[rindex];
                if (rindex_shown != 0 && fp.Serial == pktq.Serial)
                    return fp.BytePosition;
                else
                    return -1;
            }

        }

        public class Decoder
        {
            public AVPacket pkt;
            public AVPacket pkt_temp;
            public PacketQueue PacketQueue;
            public AVCodecContext* avctx;
            public int PacketSerial;
            public bool IsFinished;
            public bool IsPacketPending;
            public SDL_cond empty_queue_cond;
            public long StartPts;
            public AVRational StartPtsTimebase;
            public long NextPts;
            public AVRational NextPtsTimebase;
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
