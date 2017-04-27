namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// A Group of sequential packets
    /// Port of PacketQueue
    /// </summary>
    internal unsafe class PacketQueue
    {

        #region State Management and Constants

        internal static AVPacket* FlushPacket { get; set; }
        internal const int MaxQueueByteLength = (15 * 1024 * 1024); // 15 MB

        internal readonly MonitorLock SyncLock;
        internal readonly LockCondition IsDoneWriting;

        /// <summary>
        /// Initializes the <see cref="PacketQueue"/> static class.
        /// Allocates memory for an empty packet
        /// </summary>
        static PacketQueue()
        {
            var newPacket = new AVPacket();
            FlushPacket = &newPacket;
            ffmpeg.av_init_packet(FlushPacket);
            FlushPacket->data = (byte*)&newPacket;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the first packet holder in the queue.
        /// </summary>
        public PacketHolder First { get; private set; }

        /// <summary>
        /// Gets the last packet holder in the queue.
        /// </summary>
        public PacketHolder Last { get; private set; }

        /// <summary>
        /// Gets the number of items in the queue.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the length of bytes of this queue.
        /// </summary>
        public int ByteLength { get; private set; }

        /// <summary>
        /// Gets the total duration of the packets ocntained in this queue.
        /// </summary>
        public long Duration { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this queue has been instructed to stop
        /// enqueing items via the Abot method.
        /// </summary>
        public bool IsAborted { get; private set; }

        /// <summary>
        /// Gets the current packet serial.
        /// </summary>
        public int Serial { get; private set; }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="PacketQueue" /> class.
        /// Port of packet_queue_init
        /// Port of packet_queue_start
        /// </summary>
        public PacketQueue()
        {
            SyncLock = new MonitorLock();
            IsDoneWriting = new LockCondition();

            try
            {
                SyncLock.Lock();
                IsAborted = false;
                EnqueueInternal(FlushPacket);
            }
            finally
            {
                SyncLock.Unlock();
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Enqueues the internal.
        /// Port of packet_queue_put_private
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <returns></returns>
        private int EnqueueInternal(AVPacket* packet)
        {
            if (IsAborted)
                return -1;

            var currentPacket = new PacketHolder();
            currentPacket.Packet = packet;
            currentPacket.Next = null;
            if (packet == PacketQueue.FlushPacket)
                Serial++;

            currentPacket.Serial = Serial;

            if (Last == null)
                First = currentPacket;
            else
                Last.Next = currentPacket;

            Last = currentPacket;
            Count++;
            ByteLength += currentPacket.Packet->size + PacketHolder.SizeOf; // + sizeof(*pkt1);
            Duration += currentPacket.Packet->duration;

            IsDoneWriting.Signal();
            return 0;
        }

        public int EnqueueFlushPacket()
        {
            return Enqueue(FlushPacket);
        }

        /// <summary>
        /// Enqueues the specified packet.
        /// Port of packet_queue_put
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <returns></returns>
        public int Enqueue(AVPacket* packet)
        {
            var result = default(int);

            try
            {
                SyncLock.Lock();
                result = EnqueueInternal(packet);
            }
            finally
            {
                SyncLock.Unlock();
            }

            if (packet != PacketQueue.FlushPacket && result < 0)
                ffmpeg.av_packet_unref(packet);

            return result;
        }

        /// <summary>
        /// Enqueues an empty packet.
        /// Port of packet_queue_put_nullpacket
        /// </summary>
        /// <param name="streamIndex">Index of the stream.</param>
        /// <returns></returns>
        public int EnqueueEmptyPacket(int streamIndex)
        {
            var packet = new AVPacket();
            ffmpeg.av_init_packet(&packet);
            packet.data = null;
            packet.size = 0;
            packet.stream_index = streamIndex;

            return Enqueue(&packet);
        }

        /// <summary>
        /// Clears all the items in the queue
        /// Port of packet_queue_flush
        /// </summary>
        public void Clear()
        {
            PacketHolder currentNode = null;
            PacketHolder nextNode = null;

            try
            {
                SyncLock.Lock();

                for (currentNode = First; currentNode != null; currentNode = nextNode)
                {
                    nextNode = currentNode.Next;
                    ffmpeg.av_packet_unref(currentNode.Packet);
                }

                Last = null;
                First = null;
                Count = 0;
                ByteLength = 0;
                Duration = 0;
            }
            finally
            {
                SyncLock.Unlock();
            }
        }

        /// <summary>
        /// Dequeues the specified packet.
        /// Also sets the PacketSerial of the Decoder
        /// Port of packet_queue_get
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="packetSerial">The serial.</param>
        /// <returns></returns>
        public int Dequeue(AVPacket* packet, ref int packetSerial)
        {
            PacketHolder node = null;
            var result = default(int);

            try
            {
                SyncLock.Lock();

                while (true)
                {
                    if (IsAborted)
                    {
                        result = -1;
                        break;
                    }

                    node = First;
                    if (node != null)
                    {
                        First = node.Next;
                        if (First == null)
                            Last = null;

                        Count--;
                        ByteLength -= node.Packet->size + PacketHolder.SizeOf; // + sizeof(*pkt1)
                        Duration -= node.Packet->duration;

                        packet = node.Packet;

                        if (packetSerial != 0)
                            packetSerial = node.Serial;

                        result = 1;
                        break;
                    }
                    else
                    {
                        IsDoneWriting.Wait(SyncLock);
                    }
                }
            }
            finally
            {
                SyncLock.Unlock();
            }

            return result;
        }

        /// <summary>
        /// Aborts this queue. This prevents items from being enqueued.
        /// Port of: packet_queue_abort
        /// </summary>
        public void Abort()
        {
            try
            {
                SyncLock.Lock();
                IsAborted = true;
                IsDoneWriting.Signal();
            }
            finally
            {
                SyncLock.Unlock();
            }
        }

        public bool HasEnoughPackets(AVStream* stream, int streamIndex)
        {
            return
                (streamIndex < 0) ||
                (IsAborted) ||
                ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) ||
                Count > Constants.MinFrames && (Duration == 0 ||
                ffmpeg.av_q2d(stream->time_base) * Duration > 1.0);
        }

        #endregion
    }

}
