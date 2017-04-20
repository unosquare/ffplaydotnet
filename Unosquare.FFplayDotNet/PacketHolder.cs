﻿namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;

    /// <summary>
    /// Represents a node in a linked list of packets
    /// </summary>
    public class PacketHolder
    {
        /// <summary>
        /// The pointer to the FFmpeg packet
        /// </summary>
        internal unsafe AVPacket* Packet;

        /// <summary>
        /// Gets or sets the next node in the linked list.
        /// </summary>
        public PacketHolder Next { get; set; }

        /// <summary>
        /// Gets or sets the serial number of the packet.
        /// </summary>
        public int Serial { get; set; }
    }
}
