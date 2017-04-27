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

        /// <summary>
        /// Releases unmanaged frame date from the provided frame wrapper.
        /// Port of frame_queue_unref_item
        /// </summary>
        /// <param name="frame">The frame.</param>
        private static void ResetDecodedFrame(FrameHolder frame)
        {
            if (frame.DecodedFrame != null)
                ffmpeg.av_frame_unref(frame.DecodedFrame);

            if (frame.Subtitle != null)
                ffmpeg.avsubtitle_free(frame.Subtitle);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameQueue"/> class.
        /// Port of frame_queue_init
        /// </summary>
        /// <param name="queue">The queue.</param>
        /// <param name="maxSize">The maximum size.</param>
        /// <param name="keepLast">if set to <c>true</c> [keep last].</param>
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
                var currentFrame = Frames[i];
                ResetDecodedFrame(currentFrame);
                fixed (AVFrame** framePtr = &currentFrame.DecodedFrame)
                    ffmpeg.av_frame_free(framePtr);

                currentFrame.ReleaseBitmapData(false);
            }

            GC.Collect();
            SyncLock.Destroy();
            IsDoneWriting.Dispose();
        }

        public void SignalDoneWriting()
        {
            SignalDoneWriting(null);
        }

        public void SignalDoneWriting(Action onAfterLock)
        {
            try
            {
                SyncLock.Lock();
                onAfterLock?.Invoke();
                IsDoneWriting.Signal();
            }
            finally
            {
                SyncLock.Unlock();
            }

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
            try
            {
                SyncLock.Lock();
                while (Length >= Capacity && !Packets.IsAborted)
                    IsDoneWriting.Wait(SyncLock);
            }
            finally
            {
                SyncLock.Unlock();
            }

            if (Packets.IsAborted)
                return null;

            return Frames[WriteIndex];
        }

        public FrameHolder PeekReadableFrame()
        {
            try
            {
                SyncLock.Lock();
                while (Length - ReadIndexShown <= 0 && !Packets.IsAborted)
                    IsDoneWriting.Wait(SyncLock);
            }
            finally
            {
                SyncLock.Unlock();
            }

            if (Packets.IsAborted)
                return null;

            return Frames[(ReadIndex + ReadIndexShown) % Capacity];
        }

        public void QueueNextWrite()
        {
            if (++WriteIndex == Capacity)
                WriteIndex = 0;

            try
            {
                SyncLock.Lock();
                Length++;
                IsDoneWriting.Signal();
            }
            finally
            {
                SyncLock.Unlock();
            }
        }

        public void QueueNextRead()
        {
            if (KeepLast && !Convert.ToBoolean(ReadIndexShown))
            {
                ReadIndexShown = 1;
                return;
            }

            ResetDecodedFrame(Frames[ReadIndex]);
            if (++ReadIndex == Capacity)
                ReadIndex = 0;

            try
            {
                SyncLock.Lock();
                Length--;
                IsDoneWriting.Signal();
            }
            finally
            {
                SyncLock.Unlock();
            }
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
