using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Unosquare.FFplayDotNet.SDL;

namespace Unosquare.FFplayDotNet
{

    public unsafe class FrameQueue
    {
        private readonly PacketQueue Packets = null;

        public FrameHolder[] Frames { get; } = new FrameHolder[Constants.FrameQueueSize];

        public int ReadIndex { get; private set; }
        public int WriteIndex { get; private set; }
        public int Length { get; private set; }
        public int Capacity { get; private set; }
        public bool KeepLast { get; private set; }
        public int ReadIndexShown { get; private set; }

        internal readonly MonitorLock mutex;
        internal readonly LockCondition cond;

        private static void DestroyFrame(FrameHolder vp)
        {
            ffmpeg.av_frame_unref(vp.DecodedFrame);
            fixed (AVSubtitle* vpsub = &vp.Subtitle)
            {
                ffmpeg.avsubtitle_free(vpsub);
            }
        }

        internal FrameQueue(PacketQueue queue, int maxSize, bool keepLast)
        {
            mutex = new MonitorLock();
            cond = new LockCondition();

            Packets = queue;
            Capacity = Math.Min(maxSize, Constants.FrameQueueSize);
            KeepLast = keepLast;
            for (var i = 0; i < Capacity; i++)
                Frames[i].DecodedFrame = ffmpeg.av_frame_alloc();
        }

        public void Clear()
        {
            for (var i = 0; i < Capacity; i++)
            {
                var vp = Frames[i];
                DestroyFrame(vp);
                fixed (AVFrame** framePtr = &vp.DecodedFrame)
                {
                    ffmpeg.av_frame_free(framePtr);
                }

                FFplay.free_picture(vp);
            }

            mutex.Destroy();
            cond.Dispose();
        }

        public void frame_queue_signal()
        {
            mutex.Lock();
            cond.Signal();
            mutex.Unlock();
        }

        public FrameHolder Current
        {
            get
            {
                return Frames[(ReadIndex + ReadIndexShown) % Capacity];
            }
        }

        public FrameHolder Next
        {
            get
            {
                return Frames[(ReadIndex + ReadIndexShown + 1) % Capacity];
            }
        }

        public FrameHolder Last
        {
            get
            {
                return Frames[ReadIndex];
            }
        }

        public FrameHolder PeekWritableFrame()
        {
            mutex.Lock();

            while (Length >= Capacity && !Packets.IsAborted)
            {
                cond.Wait(mutex);
            }

            mutex.Unlock();

            if (Packets.IsAborted)
                return null;

            return Frames[WriteIndex];
        }

        public FrameHolder PeekReadableFrame()
        {
            mutex.Lock();
            while (Length - ReadIndexShown <= 0 && !Packets.IsAborted)
            {
                cond.Wait(mutex);
            }

            mutex.Unlock();

            if (Packets.IsAborted)
                return null;

            return Frames[(ReadIndex + ReadIndexShown) % Capacity];
        }

        public void QueueNextWrite()
        {
            if (++WriteIndex == Capacity)
                WriteIndex = 0;

            mutex.Lock();
            Length++;

            cond.Signal();
            mutex.Unlock();
        }

        public void QueueNextRead()
        {
            if (KeepLast && !Convert.ToBoolean(ReadIndexShown))
            {
                ReadIndexShown = 1;
                return;
            }

            DestroyFrame(Frames[ReadIndex]);
            if (++ReadIndex == Capacity)
                ReadIndex = 0;

            mutex.Lock();

            Length--;

            cond.Signal();
            mutex.Unlock();
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

}
