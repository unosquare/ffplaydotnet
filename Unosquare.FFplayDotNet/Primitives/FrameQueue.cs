namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using System;
    using Unosquare.FFplayDotNet.Core;

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

        internal readonly MonitorLock SyncLock;
        internal readonly LockCondition IsDoneWriting;

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
            SyncLock = new MonitorLock();
            IsDoneWriting = new LockCondition();

            Packets = queue;
            Capacity = Math.Min(maxSize, Constants.FrameQueueSize);
            KeepLast = keepLast;

            for (var i = 0; i < Capacity; i++)
            {
                Frames[i] = new FrameHolder();
                Frames[i].DecodedFrame = ffmpeg.av_frame_alloc();
            }
                
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

            SyncLock.Destroy();
            IsDoneWriting.Dispose();
        }

        public void SignalDoneWriting()
        {
            SignalDoneWriting(null);
        }

        public void SignalDoneWriting(Action onAfterLock)
        {
            SyncLock.Lock();
            onAfterLock?.Invoke();
            IsDoneWriting.Signal();
            SyncLock.Unlock();
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
            SyncLock.Lock();

            while (Length >= Capacity && !Packets.IsAborted)
                IsDoneWriting.Wait(SyncLock);

            SyncLock.Unlock();

            if (Packets.IsAborted)
                return null;

            return Frames[WriteIndex];
        }

        public FrameHolder PeekReadableFrame()
        {
            SyncLock.Lock();

            while (Length - ReadIndexShown <= 0 && !Packets.IsAborted)
                IsDoneWriting.Wait(SyncLock);

            SyncLock.Unlock();

            if (Packets.IsAborted)
                return null;

            return Frames[(ReadIndex + ReadIndexShown) % Capacity];
        }

        public void QueueNextWrite()
        {
            if (++WriteIndex == Capacity)
                WriteIndex = 0;

            SyncLock.Lock();
            Length++;

            IsDoneWriting.Signal();
            SyncLock.Unlock();
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

            SyncLock.Lock();

            Length--;

            IsDoneWriting.Signal();
            SyncLock.Unlock();
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
