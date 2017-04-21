namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;

    // TODO: Replace locks with the original ffplay locks.

    internal unsafe class PacketQueue
    {
        internal static readonly AVPacket* FlushPacket = null;

        static PacketQueue()
        {
            ffmpeg.av_init_packet(FlushPacket);
            FlushPacket->data = (byte*)FlushPacket;
        }

        public PacketHolder FirstNode { get; private set; }
        public PacketHolder LastNode { get; private set; }

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

                var node = new PacketHolder();
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

            PacketHolder currentNode;
            PacketHolder nextNode;

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
            PacketHolder node = null;
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

}
