namespace Unosquare.FFplayDotNet
{
    using Core;
    using Swan;
    using System;
    using System.Collections.Generic;
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

        private readonly MediaBlockBuffer MainBlockBuffer;

        private Task PacketReadingTask;
        private readonly CancellationTokenSource PacketReadingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim PacketReadingCycle = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim PacketReadingExit = new ManualResetEventSlim(false);

        private Task FrameDecodingTask;

        private Task BlockRenderingTask;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackController"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        public PlaybackController(MediaContainer container)
        {
            Container = container;
            foreach (var component in Container.Components.All)
                BlockBuffers[component.MediaType] =
                    new MediaBlockBuffer(BlockBufferCounts[component.MediaType], component.MediaType);

            MainBlockBuffer = BlockBuffers[Container.Components.Main.MediaType];
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
                //$"{BlockBuffer.Debug()}".Debug(typeof(MediaContainer));
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
            while (CanReadMoreBlocks && MainBlockBuffer.CapacityPercent < 0.5d)
            {
                PacketReadingCycle.Wait(5);
                AddNextBlock();
            }

            if (setClock)
                Clock.Position = MainBlockBuffer.RangeStartTime;
        }

        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            $"BLK: {block.StartTime.Debug()} | CLK: {clockPosition.Debug()} | DFT: {drift.Debug()} | RIX: {renderIndex,6} | FQ: {Frames.Count(),6} | PQ: {Container.Components.PacketBufferLength / 1024d,6:0.00} KB".Info(typeof(MediaContainer));
        }

        #endregion

        public void Test()
        {
            PacketReadingTask = RunPacketReadingTask();
            FrameDecodingTask = RunFrameDecodingTask();
            BlockRenderingTask = RunBlockRenderingTask();

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
                PacketReadingExit.Reset();
                var packetsRead = 0;

                while (!PacketReadingCancel.IsCancellationRequested)
                {
                    // Enter a read cycle
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
                PacketReadingExit.Set();

            }, PacketReadingCancel.Token);
        }

        private Task RunFrameDecodingTask()
        {
            return Task.Run(() =>
            {
                while (true)
                {
                    // Decode Frames if necessary
                    if (Frames.Count() < MaxFrameQueueCount && Container.Components.PacketBufferCount > 0)
                    {
                        // Decode an enqueue what is possible
                        var frames = Container.Decode();
                        foreach (var frame in frames)
                            Frames.Push(frame);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

            });
        }

        private Task RunBlockRenderingTask()
        {
            return Task.Run(() =>
            {
                var lastRenderTime = TimeSpan.MinValue;
                var clockPosition = Clock.Position;
                var hasRendered = false;
                var renderIndex = -1;
                MediaBlock renderBlock = null;

                BufferBlocks(MaxPacketBufferLength / 8, true);
                Clock.Play();

                while (true)
                {
                    clockPosition = Clock.Position;
                    renderIndex = MainBlockBuffer.IndexOf(clockPosition);
                    renderBlock = MainBlockBuffer[renderIndex];
                    hasRendered = false;

                    // Check for out-of sync errors
                    if (MainBlockBuffer.IsInRange(clockPosition) == false)
                    {
                        $"SYNC ERROR - Setting CLK from {Clock.Position.Debug()} to {MainBlockBuffer.RangeStartTime.Debug()}".Warn(typeof(MediaContainer));
                        BufferBlocks(MaxPacketBufferLength / 4, true);
                    }

                    // render the frame if we have not rendered
                    if (renderBlock.StartTime != lastRenderTime)
                    {
                        lastRenderTime = renderBlock.StartTime;
                        hasRendered = true;
                        RenderBlock(renderBlock, clockPosition, renderIndex);
                    }

                    // Add the next block if the conditions require us to do so:
                    // If rendered, then we need to discard the oldest and add the newest
                    // If the render index is greater than half, the capacity, add a new block
                    while (hasRendered || renderIndex + 1 > MainBlockBuffer.Capacity / 2)
                    {
                        hasRendered = false;
                        renderIndex = MainBlockBuffer.IndexOf(clockPosition);
                        AddNextBlock();

                        // Stop the loop if we can't reach the conditions.
                        if (Frames.Count() == 0) break;
                    }

                    // Detect end of block rendering
                    if (CanReadMoreBlocks == false && renderIndex == MainBlockBuffer.Count - 1)
                    {
                        // Rendered all and nothing else to read
                        Clock.Pause();
                        break;
                    }

                    Thread.Sleep(2);
                }
            });
        }

        #endregion
    }
}
