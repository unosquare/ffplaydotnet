namespace Unosquare.FFplayDotNet
{
    using Core;
    using Swan;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    public class PlaybackController
    {
        #region Constants

        private const int MaxPacketBufferLength = 1024 * 1024 * 4;
        private const int PacketReadBatchCount = 10;
        private const int MaxFrameQueueCount = 24;

        #endregion

        #region Private Members

        private MediaContainer Container;
        private Clock Clock = new Clock();

        private readonly Dictionary<MediaType, MediaFrameQueue> Frames = new Dictionary<MediaType, MediaFrameQueue>();
        private readonly Dictionary<MediaType, MediaBlockBuffer> Blocks = new Dictionary<MediaType, MediaBlockBuffer>();
        private readonly Dictionary<MediaType, int> BlockBufferCounts = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, MaxFrameQueueCount / 2 },
            { MediaType.Audio, MaxFrameQueueCount },
            { MediaType.Subtitle, MaxFrameQueueCount }
        };

        private Task PacketReadingTask;
        private readonly CancellationTokenSource PacketReadingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim PacketReadingCycle = new ManualResetEventSlim(false);

        private Task FrameDecodingTask;
        private readonly CancellationTokenSource FrameDecodingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim FrameDecodingCycle = new ManualResetEventSlim(false);

        private Task BlockRenderingTask;
        private readonly CancellationTokenSource BlockRenderingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim BlockRenderingCycle = new ManualResetEventSlim(false);

        private readonly ManualResetEventSlim SeekingDone = new ManualResetEventSlim(true);

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackController"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        public PlaybackController(MediaContainer container)
        {
            Container = container;
            foreach (var mediaType in Container.Components.MediaTypes)
            {
                Blocks[mediaType] =
                    new MediaBlockBuffer(BlockBufferCounts[mediaType], mediaType);
                Frames[mediaType] =
                    new MediaFrameQueue();
            }

        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether this instance can read more packets.
        /// </summary>
        private bool CanReadMorePackets { get { return Container.IsAtEndOfStream == false; } }

        private bool CanReadMoreFrames { get { return Container.Components.PacketBufferLength > 0 || CanReadMorePackets; } }

        private bool CanReadMoreBlocks { get { return Frames.Any(f => f.Value.Count > 0) || CanReadMoreFrames || CanReadMorePackets; } }

        private bool CanReadMoreBlocksOf(MediaType t) { return Frames[t].Count > 0 || CanReadMoreFrames || CanReadMorePackets; }


        #endregion

        #region Methods

        private void AddNextBlock(MediaType t)
        {
            var frame = Frames[t].Dequeue();
            if (frame == null) return;
            Blocks[frame.MediaType].Add(frame, Container);
        }

        private void BufferBlocks(int packetBufferLength, bool setClock)
        {
            // Buffer some packets
            while (CanReadMorePackets && Container.Components.PacketBufferLength < packetBufferLength)
                PacketReadingCycle.Wait(5);

            // Buffer some blocks
            while (CanReadMoreBlocks && Blocks.All(b => b.Value.CapacityPercent < 0.5d))
            {
                PacketReadingCycle.Wait(5);
                foreach (var t in Container.Components.MediaTypes)
                    AddNextBlock(t);
            }

            if (setClock)
                Clock.Position = Blocks[Container.Components.Main.MediaType].RangeStartTime;
        }

        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            $"{block.MediaType.ToString().Substring(0, 1)} BLK: {block.StartTime.Debug()} | CLK: {clockPosition.Debug()} | DFT: {drift.Debug()} | RIX: {renderIndex,4} | FQ: {Frames[block.MediaType].Count,4} | PQ: {Container.Components[block.MediaType].PacketBufferLength / 1024d,7:0.00} KB".Info(typeof(MediaContainer));
        }

        #endregion

        public void Seek(TimeSpan position)
        {
            SeekingDone.Wait();
            var startTime = DateTime.UtcNow;
            var resumeClock = Clock.IsRunning;
            Clock.Pause();
            SeekingDone.Reset();
            PacketReadingCycle.Wait();
            FrameDecodingCycle.Wait();
            BlockRenderingCycle.Wait();

            // Clear both, frames and blocks
            foreach (var fr in Frames)
                fr.Value.Clear();

            foreach (var block in Blocks)
                block.Value.Clear();

            // Populate frame with after-seek operation
            var frames = Container.Seek(position);
            foreach (var frame in frames)
                Frames[frame.MediaType].Push(frame);

            if (resumeClock)
                Clock.Play();

            $"SEEK D: Elapsed: {startTime.DebugElapsedUtc()}".Debug(typeof(MediaContainer));

            SeekingDone.Set();
        }

        public void Test()
        {
            PacketReadingTask = RunPacketReadingTask();
            FrameDecodingTask = RunFrameDecodingTask();
            BlockRenderingTask = RunBlockRenderingTask();

            // Test seeking
            while (true)
            {
                if (Clock.Position.TotalSeconds >= 3)
                {
                    Seek(TimeSpan.FromSeconds(30));
                    break;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            BlockRenderingTask.Wait();
            $"Finished rendering everything!".Warn(typeof(MediaContainer));

        }

        #region Task Runners

        /// <summary>
        /// Runs the read task which keeps a packet buffer healthy.
        /// </summary>
        private Task RunPacketReadingTask()
        {
            return Task.Run(() =>
            {
                var packetsRead = 0;

                while (!PacketReadingCancel.IsCancellationRequested)
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

                    PacketReadingCycle.Set();

                    if (!CanReadMorePackets || Container.Components.PacketBufferLength > MaxPacketBufferLength)
                        Thread.Sleep(1);

                }

                PacketReadingCycle.Set();

            }, PacketReadingCancel.Token);
        }

        private Task RunFrameDecodingTask()
        {
            return Task.Run(() =>
            {
                while (FrameDecodingCancel.IsCancellationRequested == false)
                {
                    SeekingDone.Wait();
                    FrameDecodingCycle.Reset();

                    // Decode Frames if necessary
                    var decodedFrames = 0;

                    foreach (var component in Container.Components.All)
                    {
                        if (Frames[component.MediaType].Count >= MaxFrameQueueCount)
                            continue;

                        if (component.PacketBufferCount <= 0)
                            continue;

                        var frames = component.DecodeNextPacket();
                        foreach (var frame in frames)
                        {
                            Frames[frame.MediaType].Push(frame);
                            decodedFrames += 1;
                        } 
                    }

                    FrameDecodingCycle.Set();
                    if (decodedFrames <= 0)
                        Thread.Sleep(1);

                }

                FrameDecodingCycle.Set();

            }, FrameDecodingCancel.Token);
        }

        private Task RunBlockRenderingTask()
        {
            return Task.Run(() =>
            {
                var mediaTypeCount = Container.Components.MediaTypes.Length;
                var main = Container.Components.Main.MediaType;

                var clockPosition = Clock.Position;

                var lastRenderTime = new Dictionary<MediaType, TimeSpan>(mediaTypeCount);
                var hasRendered = new Dictionary<MediaType, bool>(mediaTypeCount);
                var renderIndex = new Dictionary<MediaType, int>(mediaTypeCount);
                var renderBlock = new Dictionary<MediaType, MediaBlock>(mediaTypeCount);

                foreach (var t in Container.Components.MediaTypes)
                {
                    lastRenderTime[t] = TimeSpan.MinValue;
                    hasRendered[t] = false;
                    renderIndex[t] = -1;
                    renderBlock[t] = null;
                }

                BufferBlocks(MaxPacketBufferLength / 8, true);
                Clock.Play();

                while (BlockRenderingCancel.IsCancellationRequested == false)
                {
                    SeekingDone.Wait();
                    BlockRenderingCycle.Reset();

                    // Capture current time and render index
                    clockPosition = Clock.Position;
                    renderIndex[main] = Blocks[main].IndexOf(clockPosition);

                    // Check for out-of sync issues (i.e. after seeking)
                    if (Blocks[main].IsInRange(clockPosition) == false || renderIndex[main] < 0)
                    {
                        BufferBlocks(MaxPacketBufferLength / 4, true);
                        $"SYNC              CLK: {clockPosition.Debug()} | TGT: {Blocks[main].RangeStartTime.Debug()}".Warn(typeof(MediaContainer));
                        clockPosition = Clock.Position;
                        renderIndex[main] = Blocks[main].IndexOf(clockPosition);
                    }

                    foreach (var t in Container.Components.MediaTypes)
                    {
                        var blocks = Blocks[t];
                        renderIndex[t] = blocks.IndexOf(clockPosition);

                        // Retrieve the render block
                        renderBlock[t] = blocks[renderIndex[t]];
                        hasRendered[t] = false;

                        // render the frame if we have not rendered
                        if (renderBlock[t].StartTime != lastRenderTime[t])
                        {
                            lastRenderTime[t] = renderBlock[t].StartTime;
                            hasRendered[t] = true;
                            RenderBlock(renderBlock[t], clockPosition, renderIndex[t]);
                        }

                        // Add the next block if the conditions require us to do so:
                        // If rendered, then we need to discard the oldest and add the newest
                        // If the render index is greater than half, the capacity, add a new block
                        while (hasRendered[t] || renderIndex[t] + 1 > Blocks[t].Capacity / 2)
                        {
                            AddNextBlock(t);
                            hasRendered[t] = false;
                            renderIndex[t] = Blocks[t].IndexOf(clockPosition);

                            // Stop the loop if we can't reach the conditions.
                            if (Frames[t].Count == 0)
                                break;
                        }
                    }

                    // Detect end of block rendering
                    if (CanReadMoreBlocksOf(main) == false && renderIndex[main] == Blocks[main].Count - 1)
                    {
                        // Rendered all and nothing else to read
                        Clock.Pause();
                        break;
                    }

                    BlockRenderingCycle.Set();
                    Thread.Sleep(2);
                }

                BlockRenderingCycle.Set();

            }, BlockRenderingCancel.Token);
        }

        #endregion
    }
}
