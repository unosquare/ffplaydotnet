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
            public AudioParams()
            {

            }

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
            #region Private Declarations

            private double m_SpeedRatio = default(double);
            private readonly Func<int?> GetPacketQueueSerial; /* pointer to the current packet queue serial, used for obsolete clock detection */
            private double PtsDrift;    /* clock base minus time at which we updated the clock */

            #endregion

            #region Constructors

            public Clock(Func<int?> getPacketQueueSerialDelegate)
            {
                SpeedRatio = 1.0;
                IsPaused = false;
                GetPacketQueueSerial = getPacketQueueSerialDelegate;
                SetPosition(double.NaN, -1);
            }

            #endregion

            #region Properties

            public bool IsPaused { get; set; }
            public double Pts { get; private set; }           /* clock base */
            public double LastUpdated { get; private set; }
            public int PacketSerial { get; private set; }           /* clock is based on a packet with this serial */
            public double SpeedRatio
            {
                get
                {
                    return m_SpeedRatio;
                }
                set
                {
                    SetPosition(Position, PacketSerial);
                    m_SpeedRatio = value;
                }
            }
            public int? PacketQueueSerial
            {
                get
                {
                    if (GetPacketQueueSerial == null) return PacketSerial;
                    return GetPacketQueueSerial();
                }
            }
            public double Position
            {
                get
                {
                    if (GetPacketQueueSerial().HasValue == false || GetPacketQueueSerial().Value != PacketSerial)
                        return double.NaN;

                    if (IsPaused)
                    {
                        return Pts;
                    }
                    else
                    {
                        var time = ffmpeg.av_gettime_relative() / 1000000.0;
                        return PtsDrift + time - (time - LastUpdated) * (1.0 - SpeedRatio);
                    }
                }
            }

            #endregion

            #region Methods

            public void SetPosition(double pts, int serial, double time)
            {
                Pts = pts;
                LastUpdated = time;
                PtsDrift = Pts - time;
                PacketSerial = serial;
            }

            public void SetPosition(double pts, int serial)
            {
                var time = ffmpeg.av_gettime_relative() / 1000000.0;
                SetPosition(pts, serial, time);
            }

            public void SyncTo(Clock slave)
            {
                var currentPosition = Position;
                var slavePosition = slave.Position;
                if (!double.IsNaN(slavePosition) && (double.IsNaN(currentPosition) || Math.Abs(currentPosition - slavePosition) > AV_NOSYNC_THRESHOLD))
                    SetPosition(slavePosition, slave.PacketSerial);
            }

            #endregion
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
            private readonly PacketQueue Packets = null;

            public Frame[] Frames { get; } = new Frame[FRAME_QUEUE_SIZE];


            public int ReadIndex { get; private set; }
            public int WriteIndex { get; private set; }
            public int Length;
            public int Capacity;
            public bool KeepLast;
            public int ReadIndexShown;
            public SDL_mutex mutex;
            public SDL_cond cond;

            private static void DestroyFrame(Frame vp)
            {
                ffmpeg.av_frame_unref(vp.DecodedFrame);
                fixed (AVSubtitle* vpsub = &vp.Subtitle)
                {
                    ffmpeg.avsubtitle_free(vpsub);
                }
            }

            public FrameQueue(PacketQueue queue, int maxSize, bool keepLast)
            {
                mutex = SDL_CreateMutex();
                cond = SDL_CreateCond();

                Packets = queue;
                Capacity = Math.Min(maxSize, FRAME_QUEUE_SIZE);
                KeepLast = keepLast;
                for (var i = 0; i < Capacity; i++)
                    Frames[i].DecodedFrame = ffmpeg.av_frame_alloc();
            }

            public void frame_queue_destory()
            {
                for (var i = 0; i < Capacity; i++)
                {
                    var vp = Frames[i];
                    DestroyFrame(vp);
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
                return Frames[(ReadIndex + ReadIndexShown) % Capacity];
            }

            public Frame frame_queue_peek_next()
            {
                return Frames[(ReadIndex + ReadIndexShown + 1) % Capacity];
            }

            public Frame frame_queue_peek_last()
            {
                return Frames[ReadIndex];
            }

            public Frame PeekWritableFrame()
            {
                SDL_LockMutex(mutex);

                while (Length >= Capacity && !Packets.IsAborted)
                {
                    SDL_CondWait(cond, mutex);
                }

                SDL_UnlockMutex(mutex);

                if (Packets.IsAborted)
                    return null;

                return Frames[WriteIndex];
            }

            public Frame PeekReadableFrame()
            {
                SDL_LockMutex(mutex);
                while (Length - ReadIndexShown <= 0 && !Packets.IsAborted)
                {
                    SDL_CondWait(cond, mutex);
                }
                SDL_UnlockMutex(mutex);
                if (Packets.IsAborted)
                    return null;
                return Frames[(ReadIndex + ReadIndexShown) % Capacity];
            }

            public void frame_queue_push()
            {
                if (++WriteIndex == Capacity)
                    WriteIndex = 0;

                SDL_LockMutex(mutex);

                Length++;

                SDL_CondSignal(cond);
                SDL_UnlockMutex(mutex);
            }

            public void frame_queue_next()
            {
                if (KeepLast && !Convert.ToBoolean(ReadIndexShown))
                {
                    ReadIndexShown = 1;
                    return;
                }

                DestroyFrame(Frames[ReadIndex]);
                if (++ReadIndex == Capacity)
                    ReadIndex = 0;

                SDL_LockMutex(mutex);

                Length--;

                SDL_CondSignal(cond);
                SDL_UnlockMutex(mutex);
            }

            public int frame_queue_nb_remaining()
            {
                return Length - ReadIndexShown;
            }

            public long StreamPosition
            {
                get
                {
                    var frame = Frames[ReadIndex];

                    if (ReadIndexShown != 0 && frame.Serial == Packets.Serial)
                        return frame.BytePosition;
                    else
                        return -1;
                }
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
            internal readonly GCHandle Handle;

            internal AVInputFormat* InputFormat;
            internal AVFormatContext* InputContext;

            internal AVStream* AudioStream;
            internal AVStream* SubtitleStream;
            internal AVStream* VideoStream;

            internal SwrContext* AudioScaler;
            internal SwsContext* VideoScaler;
            internal SwsContext* SubtitleScaler;

            public MediaState()
            {
                Handle = GCHandle.Alloc(this, GCHandleType.Pinned);
            }

            public SDL_Thread ReadThread;
            
            public bool IsAbortRequested { get; internal set; }
            public bool IsForceRefreshRequested { get; internal set; }
            public bool IsPaused { get; set; }

            public int last_paused;

            public bool queue_attachments_req;
            public bool IsSeekRequested { get; internal set; }

            public int seek_flags;
            public long seek_pos;
            public long seek_rel;
            public int read_pause_return;

            

            public bool IsMediaRealtime { get; internal set; }

            public Clock AudioClock { get; internal set; }
            public Clock VideoClock { get; internal set; }
            public Clock ExternalClock { get; internal set; }

            public FrameQueue VideoQueue { get; internal set; }
            public FrameQueue SubtitleQueue { get; internal set; }
            public FrameQueue AudioQueue { get; internal set; }

            public Decoder AudioDecoder { get; } = new Decoder();
            public Decoder VideoDecoder { get; } = new Decoder();
            public Decoder SubtitleDecoder { get; } = new Decoder();

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

            public double AudioClockPosition { get; internal set; }
            public int AudioClockSerial { get; internal set; }

            public double audio_diff_cum; /* used for AV difference average computation */
            public double audio_diff_avg_coef;
            public double audio_diff_threshold;
            public int audio_diff_avg_count;

            public PacketQueue VideoPackets { get; } = new PacketQueue();
            public PacketQueue AudioPackets { get; } = new PacketQueue();
            public PacketQueue SubtitlePackets { get; } = new PacketQueue();

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

            public SDL_cond continue_read_thread;
        }

        #endregion

    }
}
