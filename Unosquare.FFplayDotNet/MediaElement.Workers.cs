namespace Unosquare.FFplayDotNet
{
    using Core;
    using Decoding;
    using Rendering;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Windows.Controls;

    partial class MediaElement
    {
        /// <summary>
        /// This partial class implements: 
        /// 1. Packet reading from the Container
        /// 2. Frame Decoding from packet buffer
        /// 3. Block Rendering from frame queue
        /// </summary>

        #region Constants

        private static readonly int StateDictionaryCapacity = Constants.MediaTypes.Count - 1;
        private const int MaxPacketBufferLength = 1024 * 1024 * 8; // TODO: 8MB buffer adjust according to the bitrate if available.
        private const int WaitPacketBufferLength = 512 * 1024; // TODO: adjust this to a multiple of bitrate if available
        private const int PacketReadBatchCount = 10; // Read 10 packets at a time

        private static readonly Dictionary<MediaType, int> MaxBlocks
            = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, 12 },
            { MediaType.Audio, 24 },
            { MediaType.Subtitle, 48 }
        };

        private static readonly Dictionary<MediaType, int> MaxFrames
            = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, 24 },
            { MediaType.Audio, 48 },
            { MediaType.Subtitle, 48 }
        };

        #endregion

        #region State Variables

        private readonly ObjectDictionary<MediaType, MediaFrameQueue> Frames
            = new ObjectDictionary<MediaType, MediaFrameQueue>(StateDictionaryCapacity);

        internal readonly ObjectDictionary<MediaType, MediaBlockBuffer> Blocks
            = new ObjectDictionary<MediaType, MediaBlockBuffer>(StateDictionaryCapacity);

        private readonly ObjectDictionary<MediaType, IRenderer> Renderers
            = new ObjectDictionary<MediaType, IRenderer>(StateDictionaryCapacity);

        private readonly ObjectDictionary<MediaType, TimeSpan> LastRenderTime
            = new ObjectDictionary<MediaType, TimeSpan>(StateDictionaryCapacity);

        private volatile bool IsTaskCancellationPending = false;


        private Thread PacketReadingTask;
        private readonly ManualResetEventSlim PacketReadingCycle = new ManualResetEventSlim(true);

        private Thread FrameDecodingTask;
        private readonly ManualResetEventSlim FrameDecodingCycle = new ManualResetEventSlim(true);

        private Thread BlockRenderingTask;
        private readonly ManualResetEventSlim BlockRenderingCycle = new ManualResetEventSlim(true);

        private readonly ManualResetEventSlim SeekingDone = new ManualResetEventSlim(true);
        private TimeSpan? RequestedSeekPosition = null;

        #endregion

        #region Private Properties

        /// <summary>
        /// Gets a value indicating whether more packets can be read from the stream.
        /// This does not check if the packet queue is full.
        /// </summary>
        private bool CanReadMorePackets { get { return Container.IsAtEndOfStream == false; } }

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded from the packet queue.
        /// That is, if we have packets in the packet buffer or if we are not at the end of the stream.
        /// </summary>
        private bool CanReadMoreFrames { get { return Container.Components.PacketBufferLength > 0 || CanReadMorePackets; } }

        /// <summary>
        /// Gets a value indicating whether more frames can be converted into blocks.
        /// </summary>
        private bool CanReadMoreBlocks { get { return Frames.Any(f => f.Value.Count > 0) || CanReadMoreFrames || CanReadMorePackets; } }

        #endregion

        #region Methods

        /// <summary>
        /// Gets a value indicating whether more frames can be converted into blocks of the given type.
        /// </summary>
        private bool CanReadMoreBlocksOf(MediaType t) { return Frames[t].Count > 0 || CanReadMoreFrames || CanReadMorePackets; }

        /// <summary>
        /// Dequeues the next available frame and converts it into a block of the appropriate type,
        /// adding it to the correpsonding block buffer. If there is no more blocks in the pool, then 
        /// more room is provided automatically.
        /// </summary>
        /// <param name="t">The media type.</param>
        private MediaBlock AddNextBlock(MediaType t)
        {
            var frame = Frames[t].Dequeue();
            if (frame == null) return null;

            var addedBlock = Blocks[t].Add(frame, Container);
            return addedBlock;
        }

        /// <summary>
        /// Buffers some packets which in turn get decoded into frames and then
        /// converted into blocks.
        /// </summary>
        /// <param name="packetBufferLength">Length of the packet buffer.</param>
        private void BufferBlocks(int packetBufferLength)
        {
            var main = Container.Components.Main.MediaType;

            // Raise the buffering started event.
            IsBuffering = true;
            BufferingProgress = 0;
            RaiseBufferingStartedEvent();

            // Buffer some packets
            while (CanReadMorePackets && Container.Components.PacketBufferLength < packetBufferLength)
                PacketReadingCycle.Wait(1);

            // Wait up to 1 second to decode frames. This happens much faster but 1s is plenty.
            FrameDecodingCycle.Wait(1000);

            // Buffer some blocks
            while (CanReadMoreBlocks && Blocks[main].CapacityPercent <= 0.5d)
            {
                PacketReadingCycle.Wait(1);
                FrameDecodingCycle.Wait(1);
                BufferingProgress = Blocks[main].CapacityPercent / 0.5d;
                foreach (var t in Container.Components.MediaTypes)
                    AddNextBlock(t);
            }

            // Raise the buffering started event.
            BufferingProgress = 1;
            IsBuffering = false;
            RaiseBufferingEndedEvent();
        }

        /// <summary>
        /// The render block callback that updates the reported media position
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <param name="renderIndex">Index of the render.</param>
        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            Renderers[block.MediaType].Render(block, clockPosition, renderIndex);
            Container.LogRenderBlock(block, clockPosition, renderIndex);
        }

        #endregion

        #region Workers (Reading, Decoding, Rendering)

        /// <summary>
        /// Runs the read task which keeps a packet buffer as full as possible.
        /// </summary>
        private void RunPacketReadingWorker()
        {
            var packetsRead = 0;

            while (IsTaskCancellationPending == false)
            {
                // Enter a read cycle
                SeekingDone.Wait();
                PacketReadingCycle.Reset();

                // Read a bunch of packets at a time
                packetsRead = 0;
                while (CanReadMorePackets
                    && packetsRead < PacketReadBatchCount
                    && Container.Components.PacketBufferLength < MaxPacketBufferLength)
                {
                    Container.Read();
                    packetsRead++;
                }

                DownloadProgress = Math.Min(1d, Math.Round((double)Container.Components.PacketBufferLength / MaxPacketBufferLength, 3));
                PacketReadingCycle.Set();


                if (!CanReadMorePackets || Container.Components.PacketBufferLength > MaxPacketBufferLength)
                    Thread.Sleep(1);

            }

            PacketReadingCycle.Set();
        }

        /// <summary>
        /// Continually decodes the available packet buffer to have as
        /// many frames as possible in each frame queue and 
        /// up to the MaxFrames on each component
        /// </summary>
        private void RunFrameDecodingWorker()
        {
            while (IsTaskCancellationPending == false)
            {
                // Wait for a seek operation to complete (if any)
                // and initiate a decoding cycle.
                SeekingDone.Wait();
                FrameDecodingCycle.Reset();

                // Decode Frames if necessary
                var decodedFrames = 0;

                // Decode frames for each of the components
                foreach (var component in Container.Components.All)
                {
                    // Check if we can accept more frames
                    if (Frames[component.MediaType].Count >= MaxFrames[component.MediaType])
                        continue;

                    // Don't do anything if we don't have packets to decode
                    if (component.PacketBufferCount <= 0)
                        continue;

                    // Push the decoded frames
                    var frames = component.DecodeNextPacket();
                    foreach (var frame in frames)
                    {
                        Frames[frame.MediaType].Push(frame);
                        decodedFrames += 1;
                    }
                }

                // Complete the frame decoding cycle
                FrameDecodingCycle.Set();

                // Give it a break if there wa snothing to decode.
                if (decodedFrames <= 0)
                    Thread.Sleep(1);

            }

            FrameDecodingCycle.Set();

        }

        /// <summary>
        /// Continuously converts frmes and places them on the corresponding
        /// block buffer. This task is responsible for keeping track of the clock
        /// and calling the render methods appropriate for the current clock position.
        /// </summary>
        /// <param name="control">The control.</param>
        /// <returns></returns>
        private void RunBlockRenderingWorker()
        {

            var mediaTypeCount = Container.Components.MediaTypes.Length;
            var main = Container.Components.Main.MediaType;

            var hasRendered = new Dictionary<MediaType, bool>(mediaTypeCount);
            var renderIndex = new Dictionary<MediaType, int>(mediaTypeCount);
            var renderBlock = new Dictionary<MediaType, MediaBlock>(mediaTypeCount);

            foreach (var t in Container.Components.MediaTypes)
            {
                hasRendered[t] = false;
                renderIndex[t] = -1;
                renderBlock[t] = null;
            }

            // Buffer some blocks
            BufferBlocks(WaitPacketBufferLength);
            Clock.Position = Blocks[main].RangeStartTime;
            var clockPosition = Clock.Position;

            while (IsTaskCancellationPending == false)
            {
                if (RequestedSeekPosition != null)
                    Seek(RequestedSeekPosition.Value);

                SeekingDone.Wait();
                BlockRenderingCycle.Reset();

                // Capture current time and render index
                clockPosition = Clock.Position;
                renderIndex[main] = Blocks[main].IndexOf(clockPosition);

                // Check for out-of sync issues (i.e. after seeking)
                if (Blocks[main].IsInRange(clockPosition) == false || renderIndex[main] < 0)
                {
                    BufferBlocks(WaitPacketBufferLength);
                    Clock.Position = Blocks[main].RangeStartTime;
                    Container.Log(MediaLogMessageType.Warning,
                        $"SYNC              CLK: {clockPosition.Debug()} | TGT: {Blocks[main].RangeStartTime.Debug()} | SET: {Clock.Position.Debug()}");

                    clockPosition = Clock.Position;
                    renderIndex[main] = Blocks[main].IndexOf(clockPosition);
                }

                foreach (var t in Container.Components.MediaTypes)
                {
                    var blocks = Blocks[t];
                    renderIndex[t] = blocks.IndexOf(clockPosition);

                    while (t != main && blocks.RangeEndTime <= Blocks[main].RangeStartTime && renderIndex[t] >= blocks.Count - 1)
                    {
                        if (AddNextBlock(t) == null)
                            break;
                        else
                            renderIndex[t] = blocks.IndexOf(clockPosition);
                    } 
                    
                    if (renderIndex[t] < 0)
                        continue;

                    // Retrieve the render block
                    renderBlock[t] = blocks[renderIndex[t]];
                    hasRendered[t] = false;

                    // render the frame if we have not rendered
                    if (renderBlock[t].StartTime != LastRenderTime[t]
                        && renderBlock[t].StartTime.Ticks <= clockPosition.Ticks)
                    {
                        LastRenderTime[t] = renderBlock[t].StartTime;
                        hasRendered[t] = true;
                        // Update the position;
                        if (t == main) UpdatePosition(clockPosition);
                        RenderBlock(renderBlock[t], clockPosition, renderIndex[t]);
                    }

                    // Add the next block if the conditions require us to do so:
                    // If rendered, then we need to discard the oldest and add the newest
                    // If the render index is greater than half, the capacity, add a new block
                    if (hasRendered[t])
                    {
                        while (Blocks[t].IsFull == false || renderIndex[t] + 1 > Blocks[t].Capacity / 2)
                        {
                            if (AddNextBlock(t) == null) break;
                            renderIndex[t] = blocks.IndexOf(clockPosition);
                        }

                        hasRendered[t] = false;
                        renderIndex[t] = Blocks[t].IndexOf(clockPosition);
                    }

                }

                // Detect end of block rendering
                if (CanReadMoreBlocksOf(main) == false && renderIndex[main] == Blocks[main].Count - 1)
                {
                    if (MediaState != MediaState.Pause)
                    {
                        // Rendered all and nothing else to read
                        Clock.Pause();
                        Clock.Position = Blocks[main].RangeEndTime;
                        MediaState = MediaState.Pause;
                        UpdatePosition(Clock.Position);
                        HasMediaEnded = true;
                        RaiseMediaEndedEvent();
                    }

                }
                else
                {
                    HasMediaEnded = false;
                }

                BlockRenderingCycle.Set();
                Thread.Sleep(1);
            }

            BlockRenderingCycle.Set();

        }

        #endregion

    }
}
