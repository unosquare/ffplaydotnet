namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Diagnostics;
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

        public class FrameHolder
        {
            public AVFrame* DecodedFrame;
            public AVSubtitle Subtitle;
            public int Serial;
            public double Pts;           /* presentation timestamp for the frame */
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

            public FrameHolder[] Frames { get; } = new FrameHolder[FRAME_QUEUE_SIZE];


            public int ReadIndex { get; private set; }
            public int WriteIndex { get; private set; }
            public int Length;
            public int Capacity;
            public bool KeepLast;
            public int ReadIndexShown;
            public SDL_mutex mutex;
            public SDL_cond cond;

            private static void DestroyFrame(FrameHolder vp)
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

            public FrameHolder Peek()
            {
                return Frames[(ReadIndex + ReadIndexShown) % Capacity];
            }

            public FrameHolder PeekNext()
            {
                return Frames[(ReadIndex + ReadIndexShown + 1) % Capacity];
            }

            public FrameHolder PeekLast()
            {
                return Frames[ReadIndex];
            }

            public FrameHolder PeekWritableFrame()
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

            public FrameHolder PeekReadableFrame()
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

            public int PendingCount
            {
                get
                {
                    return Length - ReadIndexShown;
                }
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
            public AVPacket CurrentPacket;
            public AVPacket pkt_temp;
            public PacketQueue PacketQueue;
            public AVCodecContext* Codec;
            public int PacketSerial;
            public bool IsFinished;
            public bool IsPacketPending;
            public SDL_cond empty_queue_cond;
            public long StartPts;
            public AVRational StartPtsTimebase;
            public long NextPts;
            public AVRational NextPtsTimebase;
            public SDL_Thread DecoderThread;
            public bool? IsPtsReorderingEnabled { get; private set; } = null;


            public Decoder(AVCodecContext* avctx, PacketQueue queue, SDL_cond empty_queue_cond3)
            {
                Codec = avctx;
                PacketQueue = queue;
                empty_queue_cond = empty_queue_cond3;
                StartPts = ffmpeg.AV_NOPTS_VALUE;
            }

            public int DecodeFrame(AVFrame* frame)
            {
                return DecodeFrame(frame, null);
            }

            public int DecodeFrame(AVSubtitle* subtitle)
            {
                return DecodeFrame(null, subtitle);
            }

            private int DecodeFrame(AVFrame* frame, AVSubtitle* subtitle)
            {
                int got_frame = 0;
                do
                {
                    int ret = -1;
                    if (PacketQueue.IsAborted)
                        return -1;

                    if (!IsPacketPending || PacketQueue.Serial != PacketSerial)
                    {
                        var pkt = new AVPacket();
                        do
                        {
                            if (PacketQueue.Length == 0)
                                SDL_CondSignal(empty_queue_cond);
                            if (PacketQueue.Dequeue(&pkt, ref PacketSerial) < 0)
                                return -1;
                            if (pkt.data == PacketQueue.FlushPacket->data)
                            {
                                ffmpeg.avcodec_flush_buffers(Codec);
                                IsFinished = false;
                                NextPts = StartPts;
                                NextPtsTimebase = StartPtsTimebase;
                            }
                        } while (pkt.data == PacketQueue.FlushPacket->data || PacketQueue.Serial != PacketSerial);
                        fixed (AVPacket* refPacket = &CurrentPacket)
                        {
                            ffmpeg.av_packet_unref(refPacket);
                        }

                        pkt_temp = CurrentPacket = pkt;
                        IsPacketPending = true;
                    }
                    switch (Codec->codec_type)
                    {
                        case AVMediaType.AVMEDIA_TYPE_VIDEO:
                            fixed (AVPacket* pktTemp = &pkt_temp)
                            {
#pragma warning disable CS0618 // Type or member is obsolete
                                ret = ffmpeg.avcodec_decode_video2(Codec, frame, &got_frame, pktTemp);
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
                            fixed (AVPacket* pktTemp = &pkt_temp)
                            {
#pragma warning disable CS0618 // Type or member is obsolete
                                ret = ffmpeg.avcodec_decode_audio4(Codec, frame, &got_frame, pktTemp);
#pragma warning restore CS0618 // Type or member is obsolete
                            }
                            if (got_frame != 0)
                            {
                                var tb = new AVRational { num = 1, den = frame->sample_rate };
                                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                                    frame->pts = ffmpeg.av_rescale_q(frame->pts, ffmpeg.av_codec_get_pkt_timebase(Codec), tb);
                                else if (NextPts != ffmpeg.AV_NOPTS_VALUE)
                                    frame->pts = ffmpeg.av_rescale_q(NextPts, NextPtsTimebase, tb);
                                if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                                {
                                    NextPts = frame->pts + frame->nb_samples;
                                    NextPtsTimebase = tb;
                                }
                            }
                            break;
                        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                            fixed (AVPacket* pktTemp = &pkt_temp)
                            {
                                ret = ffmpeg.avcodec_decode_subtitle2(Codec, subtitle, &got_frame, pktTemp);
                            }
                            break;
                    }
                    if (ret < 0)
                    {
                        IsPacketPending = false;
                    }
                    else
                    {
                        pkt_temp.dts =
                        pkt_temp.pts = ffmpeg.AV_NOPTS_VALUE;
                        if (pkt_temp.data != null)
                        {
                            if (Codec->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                                ret = pkt_temp.size;

                            pkt_temp.data += ret;
                            pkt_temp.size -= ret;
                            if (pkt_temp.size <= 0)
                                IsPacketPending = false;
                        }
                        else
                        {
                            if (got_frame == 0)
                            {
                                IsPacketPending = false;
                                IsFinished = Convert.ToBoolean(PacketSerial);
                            }
                        }
                    }
                } while (!Convert.ToBoolean(got_frame) && !IsFinished);

                return got_frame;
            }

            public void DecoderDestroy()
            {
                fixed (AVPacket* packetRef = &CurrentPacket)
                {
                    ffmpeg.av_packet_unref(packetRef);
                }

                fixed (AVCodecContext** refContext = &Codec)
                {
                    ffmpeg.avcodec_free_context(refContext);
                }
            }

            public void DecoderAbort(FrameQueue fq)
            {
                PacketQueue.Abort();
                fq.frame_queue_signal();
                SDL_WaitThread(DecoderThread, null);
                DecoderThread = null;
                PacketQueue.Clear();
            }

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


            public void AdjustExternalClockSpeedRatio()
            {
                if (VideoStreamIndex >= 0 && VideoPackets.Length <= EXTERNAL_CLOCK_MIN_FRAMES ||
                    AudioStreamIndex >= 0 && AudioPackets.Length <= EXTERNAL_CLOCK_MIN_FRAMES)
                {
                    ExternalClock.SpeedRatio = (Math.Max(EXTERNAL_CLOCK_SPEED_MIN, ExternalClock.SpeedRatio - EXTERNAL_CLOCK_SPEED_STEP));
                }
                else if ((VideoStreamIndex < 0 || VideoPackets.Length > EXTERNAL_CLOCK_MAX_FRAMES) &&
                         (AudioStreamIndex < 0 || AudioPackets.Length > EXTERNAL_CLOCK_MAX_FRAMES))
                {
                    ExternalClock.SpeedRatio = (Math.Min(EXTERNAL_CLOCK_SPEED_MAX, ExternalClock.SpeedRatio + EXTERNAL_CLOCK_SPEED_STEP));
                }
                else
                {
                    var speedRatio = ExternalClock.SpeedRatio;
                    if (speedRatio != 1.0)
                        ExternalClock.SpeedRatio = (speedRatio + EXTERNAL_CLOCK_SPEED_STEP * (1.0 - speedRatio) / Math.Abs(1.0 - speedRatio));
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
                    ev.type = FF_ALLOC_EVENT;
                    ev.user_data1 = this;
                    SDL_PushEvent(ev);
                    SDL_LockMutex(VideoQueue.mutex);

                    while (!vp.IsAllocated && !VideoPackets.IsAborted)
                    {
                        SDL_CondWait(VideoQueue.cond, VideoQueue.mutex);
                    }
                    if (VideoPackets.IsAborted && SDL_PeepEvents(ev, 1, SDL_GETEVENT, FF_ALLOC_EVENT, FF_ALLOC_EVENT) != 1)
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


            public void stream_cycle_channel(AVMediaType codec_type, FFplay ffplay)
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
                stream_component_open(stream_index, ffplay);
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

            public int stream_component_open(int stream_index, FFplay ffplay)
            {
                var ic = InputContext;
                string forced_codec_name = null;
                AVDictionaryEntry* t = null;

                int sample_rate = 0;
                int nb_channels = 0;
                long channel_layout = 0;
                int ret = 0;
                int stream_lowres = Convert.ToInt32(ffplay.lowres);
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
                    case AVMediaType.AVMEDIA_TYPE_AUDIO: last_audio_stream = stream_index; forced_codec_name = ffplay.AudioCodecName; break;
                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE: last_subtitle_stream = stream_index; forced_codec_name = ffplay.SubtitleCodecName; break;
                    case AVMediaType.AVMEDIA_TYPE_VIDEO: last_video_stream = stream_index; forced_codec_name = ffplay.VideoCodecName; break;
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

                if (ffplay.fast)
                    avctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

                if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                    avctx->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

                var opts = filter_codec_opts(ffplay.CodecOptions, avctx->codec_id, ic, ic->streams[stream_index], codec);

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

                IsAtEndOfFile = false;
                ic->streams[stream_index]->discard = AVDiscard.AVDISCARD_DEFAULT;

                switch (avctx->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        if ((ret = ffplay.audio_open(this, channel_layout, nb_channels, sample_rate, AudioOutputParams)) < 0)
                            goto fail;

                        AudioHardwareBufferSize = ret;
                        AudioOutputParams.CopyTo(AudioInputParams);
                        audio_buf_size = 0;
                        audio_buf_index = 0;
                        audio_diff_avg_coef = Math.Exp(Math.Log(0.01) / AUDIO_DIFF_AVG_NB);
                        audio_diff_avg_count = 0;
                        audio_diff_threshold = (double)(AudioHardwareBufferSize) / AudioOutputParams.BytesPerSecond;
                        AudioStreamIndex = stream_index;
                        AudioStream = ic->streams[stream_index];

                        AudioDecoder = new Decoder(avctx, AudioPackets, continue_read_thread);

                        if ((InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                            InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
                        {
                            AudioDecoder.StartPts = AudioStream->start_time;
                            AudioDecoder.StartPtsTimebase = AudioStream->time_base;
                        }

                        if ((ret = ffplay.decoder_start(AudioDecoder, ffplay.audio_thread, this)) < 0)
                            goto final;

                        SDL_PauseAudio(0);
                        break;
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        VideoStreamIndex = stream_index;
                        VideoStream = ic->streams[stream_index];
                        VideoDecoder = new Decoder(avctx, VideoPackets, continue_read_thread);
                        if ((ret = ffplay.decoder_start(VideoDecoder, ffplay.video_thread, this)) < 0)
                            goto final;
                        queue_attachments_req = true;
                        break;

                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        SubtitleStreamIndex = stream_index;
                        SubtitleStream = ic->streams[stream_index];
                        SubtitleDecoder = new Decoder(avctx, SubtitlePackets, continue_read_thread);
                        if ((ret = ffplay.decoder_start(SubtitleDecoder, ffplay.subtitle_thread, this)) < 0)
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
                    syncThreshold = Math.Max(AV_SYNC_THRESHOLD_MIN, Math.Min(AV_SYNC_THRESHOLD_MAX, delay));

                    if (!double.IsNaN(skew) && Math.Abs(skew) < MaximumFrameDuration)
                    {
                        if (skew <= -syncThreshold)
                            delay = Math.Max(0, delay + skew);
                        else if (skew >= syncThreshold && delay > AV_SYNC_FRAMEDUP_THRESHOLD)
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
        }

        #endregion

    }
}
