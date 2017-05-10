namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// Represents a set of Audio, Video and Subtitle components.
    /// This class is useful in order to group all components into 
    /// a single set. Sending packets is automatically handled by
    /// this class. This class is thread safe.
    /// </summary>
    public class MediaComponentSet : IDisposable
    {
        #region Private Declarations

        /// <summary>
        /// The synchronization root for locking
        /// </summary>
        private readonly object SyncRoot = new object();

        /// <summary>
        /// To detect redundant Dispose calls
        /// </summary>
        private bool IsDisposed = false;

        /// <summary>
        /// The internal Components
        /// </summary>
        protected readonly Dictionary<MediaType, MediaComponent> Items = new Dictionary<MediaType, MediaComponent>();

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaComponentSet"/> class.
        /// </summary>
        internal MediaComponentSet()
        {
            // prevent external initialization
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the video component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public VideoComponent Video
        {
            get
            {
                lock (SyncRoot)
                    return Items.ContainsKey(MediaType.Video) ? Items[MediaType.Video] as VideoComponent : null;
            }
        }

        /// <summary>
        /// Gets the audio component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public AudioComponent Audio
        {
            get
            {
                lock (SyncRoot)
                    return Items.ContainsKey(MediaType.Audio) ? Items[MediaType.Audio] as AudioComponent : null;
            }
        }

        /// <summary>
        /// Gets the subtitles component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public SubtitleComponent Subtitles
        {
            get
            {
                lock (SyncRoot)
                    return Items.ContainsKey(MediaType.Subtitle) ? Items[MediaType.Subtitle] as SubtitleComponent : null;
            }
        }

        /// <summary>
        /// Gets the sum of decoded frame count of all media components.
        /// </summary>
        public ulong DecodedFrameCount
        {
            get
            {
                lock (SyncRoot)
                    return (ulong)Items.Sum(s => (double)s.Value.DecodedFrameCount);
            }
        }

        /// <summary>
        /// Gets the sum of received packets of all media components.
        /// </summary>
        public ulong ReceivedPacketCount
        {
            get
            {
                lock (SyncRoot)
                    return (ulong)Items.Sum(s => (double)s.Value.ReceivedPacketCount);
            }
        }

        /// <summary>
        /// Gets the length in bytes of the packet buffer.
        /// These packets are the ones that have not been yet deecoded.
        /// </summary>
        public int PacketBufferLength
        {
            get
            {
                lock (SyncRoot)
                    return Items.Sum(s => s.Value.PacketBufferLength);
            }
        }

        /// <summary>
        /// Gets the number of packets that have not been
        /// fed to the decoders.
        /// </summary>
        public int PacketBufferCount
        {
            get
            {
                lock (SyncRoot)
                    return Items.Sum(s => s.Value.PacketBufferCount);
            }
        }

        /// <summary>
        /// Gets the smallest duration of all the component's packet buffers.
        /// </summary>
        public TimeSpan PacketBufferDuration
        {
            get
            {
                lock (SyncRoot)
                    return Items.Min(s => s.Value.PacketBufferDuration);
            }
        }

        /// <summary>
        /// Gets the number of frames available for decompression.
        /// </summary>
        public int FrameBufferCount
        {
            get
            {
                lock (SyncRoot)
                    return Items.Sum(s => s.Value.FrameBufferCount);
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a video component.
        /// </summary>
        public bool HasVideo { get { return Video != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has an audio component.
        /// </summary>
        public bool HasAudio { get { return Audio != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has a subtitles component.
        /// </summary>
        public bool HasSubtitles { get { return Subtitles != null; } }

        /// <summary>
        /// Gets or sets the <see cref="MediaComponent"/> with the specified media type.
        /// Setting a new component on an existing media type component will throw.
        /// Getting a non existing media component fro the given media type will return null.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException"></exception>
        /// <exception cref="System.ArgumentNullException">MediaComponent</exception>
        public MediaComponent this[MediaType mediaType]
        {
            get
            {
                lock (SyncRoot)
                    return Items.ContainsKey(mediaType) ? Items[mediaType] : null;
            }
            set
            {
                lock (SyncRoot)
                {
                    if (Items.ContainsKey(mediaType))
                        throw new ArgumentException($"A component for '{mediaType}' is already registered.");

                    Items[mediaType] = value ??
                        throw new ArgumentNullException($"{nameof(MediaComponent)} {nameof(value)} must not be null.");
                }
            }
        }

        /// <summary>
        /// Removes the component of specified media type (if registered).
        /// It calls the dispose method of the media component too.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        public void Remove(MediaType mediaType)
        {
            lock (SyncRoot)
            {
                if (Items.ContainsKey(mediaType) == false) return;

                try
                {
                    var component = Items[mediaType];
                    Items.Remove(mediaType);
                    component.Dispose();
                }
                catch
                { }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sends the specified packet to the correct component by reading the stream index
        /// of the packet that is being sent. No packet is sent if the provided packet is set to null.
        /// Returns true if the packet matched a component and was sent successfully. Otherwise, it returns false.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <returns></returns>
        internal unsafe bool SendPacket(AVPacket* packet)
        {
            if (packet == null)
                return false;


            foreach (var item in Items)
            {
                if (item.Value.StreamIndex == packet->stream_index)
                {
                    item.Value.SendPacket(packet);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sends an empty packet to all media components.
        /// When an EOF/EOS situation is encountered, this forces
        /// the decoders to enter drainig mode untill all frames are decoded.
        /// </summary>
        internal void SendEmptyPackets()
        {
            foreach (var item in Items)
                item.Value.SendEmptyPacket();
        }

        /// <summary>
        /// Clears the packet queues for all components.
        /// Additionally it flushes the codec buffered packets.
        /// This is useful after a seek operation is performed or a stream
        /// index is changed.
        /// </summary>
        internal void ClearPacketQueues()
        {
            foreach (var item in Items)
                item.Value.ClearPacketQueues();
        }

        /// <summary>
        /// Decodes the next available packet in the packet queue for all components.
        /// </summary>
        /// <returns></returns>
        public int DecodeNextPacket()
        {
            var result = 0;
            foreach (var component in Items)
                result += component.Value.DecodeNextPacket();

            return result;
        }

        public int DecompressNextFrame()
        {
            var result = 0;
            foreach (var component in Items)
                result += component.Value.DecompressNextFrame();

            return result;
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged)
                {
                    lock (SyncRoot)
                    {
                        var componentKeys = Items.Keys.ToArray();
                        foreach (var mediaType in componentKeys)
                            Remove(mediaType);
                    }
                }

                // free unmanaged resources (unmanaged objects) and override a finalizer below.
                // set large fields to null.

                IsDisposed = true;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}
