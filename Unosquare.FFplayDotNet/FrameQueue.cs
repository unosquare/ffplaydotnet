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

        internal FrameQueue(PacketQueue queue, int maxSize, bool keepLast)
        {
            mutex = SDL.SDL_CreateMutex();
            cond = SDL_CreateCond();

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
            SDL_DestroyMutex(mutex);
            SDL_DestroyCond(cond);
        }

        public void frame_queue_signal()
        {
            SDL_LockMutex(mutex);
            SDL_CondSignal(cond);
            SDL_UnlockMutex(mutex);
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

        public void QueueNextWrite()
        {
            if (++WriteIndex == Capacity)
                WriteIndex = 0;

            SDL_LockMutex(mutex);

            Length++;

            SDL_CondSignal(cond);
            SDL_UnlockMutex(mutex);
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

}
