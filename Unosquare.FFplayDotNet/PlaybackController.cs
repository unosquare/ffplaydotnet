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

        private MediaFrameQueue Frames = new MediaFrameQueue();
        private readonly Dictionary<MediaType, MediaBlockBuffer> BlockBuffers = new Dictionary<MediaType, MediaBlockBuffer>();
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
                BlockBuffers[mediaType] =
                    new MediaBlockBuffer(BlockBufferCounts[mediaType], mediaType);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether this instance can read more packets.
        /// </summary>
        private bool CanReadMorePackets { get { return Container.IsAtEndOfStream == false; } }

        private bool CanReadMoreFrames { get { return Container.Components.PacketBufferLength > 0 || CanReadMorePackets; } }

        private bool CanReadMoreBlocks { get { return Frames.Count() > 0 || CanReadMoreFrames || CanReadMorePackets; } }

        #endregion

        #region Methods

        private void AddNextBlock()
        {
            var frame = Frames.Dequeue();
            if (frame == null) return;
            if (BlockBuffers.ContainsKey(frame.MediaType))
            {
                BlockBuffers[frame.MediaType].Add(frame, Container);
                return;
            }

            frame.Dispose();
        }

        private void BufferBlocks(int packetBufferLength, bool setClock)
        {
            // Buffer some packets
            while (CanReadMorePackets && Container.Components.PacketBufferLength < packetBufferLength)
                PacketReadingCycle.Wait(5);

            // Buffer some blocks
            while (CanReadMoreBlocks && BlockBuffers.All(b => b.Value.CapacityPercent < 0.5d))
            {
                PacketReadingCycle.Wait(5);
                AddNextBlock();
            }

            if (setClock)
                Clock.Position = BlockBuffers[Container.Components.Main.MediaType].RangeStartTime;
        }

        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            $"{block.MediaType.ToString().Substring(0, 1)} BLK: {block.StartTime.Debug()} | CLK: {clockPosition.Debug()} | DFT: {drift.Debug()} | RIX: {renderIndex,4} | FQ: {Frames.Count(),4} | PQ: {Container.Components.PacketBufferLength / 1024d,6:0.00} KB".Info(typeof(MediaContainer));

            if (BlockBuffers[MediaType.Video].IsInRange(clockPosition) == false)
            {
                var mts = new MediaType[] { MediaType.Video }; //, MediaType.Audio };
                foreach (var mt in mts)
                    $"{mt}: {BlockBuffers[mt].RangeStartTime.Debug()} to {BlockBuffers[mt].RangeEndTime.Debug()}".Warn(typeof(MediaContainer));
            }
            
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
            Frames.Clear();
            foreach (var componentBuffer in BlockBuffers)
                componentBuffer.Value.Clear();

            // Populate frame with after-seek operation
            var frames = Container.Seek(position);
            foreach (var frame in frames)
                Frames.Push(frame);

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
                    if (Frames.Count() < MaxFrameQueueCount && Container.Components.PacketBufferCount > 0)
                    {
                        // Decode an enqueue what is possible
                        var frames = Container.Decode();
                        foreach (var frame in frames)
                            Frames.Push(frame);

                        FrameDecodingCycle.Set();
                    }
                    else
                    {
                        FrameDecodingCycle.Set();
                        Thread.Sleep(1);
                    }
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

                foreach(var t in Container.Components.MediaTypes)
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
                    renderIndex[main] = BlockBuffers[main].IndexOf(clockPosition);

                    // Check for out-of sync issues (i.e. after seeking)
                    if (BlockBuffers[main].IsInRange(clockPosition) == false || renderIndex[main] < 0)
                    {
                        BufferBlocks(MaxPacketBufferLength / 4, true);
                        $"SYNC              CLK: {clockPosition.Debug()} | TGT: {BlockBuffers[main].RangeStartTime.Debug()}".Warn(typeof(MediaContainer));
                        clockPosition = Clock.Position;
                        renderIndex[main] = BlockBuffers[main].IndexOf(clockPosition);
                    }

                    // Retrieve the render block
                    renderBlock[main] = BlockBuffers[main][renderIndex[main]];
                    hasRendered[main] = false;

                    // render the frame if we have not rendered
                    if (renderBlock[main].StartTime != lastRenderTime[main])
                    {
                        lastRenderTime[main] = renderBlock[main].StartTime;
                        hasRendered[main] = true;
                        RenderBlock(renderBlock[main], clockPosition, renderIndex[main]);
                    }

                    // Add the next block if the conditions require us to do so:
                    // If rendered, then we need to discard the oldest and add the newest
                    // If the render index is greater than half, the capacity, add a new block
                    while (hasRendered[main] || renderIndex[main] + 1 > BlockBuffers[main].Capacity / 2)
                    {
                        hasRendered[main] = false;
                        renderIndex[main] = BlockBuffers[main].IndexOf(clockPosition);
                        AddNextBlock();

                        // Stop the loop if we can't reach the conditions.
                        if (Frames.Count() == 0) break;
                    }

                    // Detect end of block rendering
                    if (CanReadMoreBlocks == false && renderIndex[main] == BlockBuffers[main].Count - 1)
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
