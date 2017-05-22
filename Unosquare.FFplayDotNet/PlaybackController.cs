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
        private MediaContainer Container;
        private Clock Clock = new Clock();

        private MediaFrameQueue Frames = new MediaFrameQueue();
        private const int MaxPacketBufferLength = 1024 * 1024 * 4;
        private const int MaxFrameQueueCount = 24;
        private const int PacketBatchCount = 10;

        private readonly Dictionary<MediaType, MediaBlockBuffer> BlockBuffers = new Dictionary<MediaType, MediaBlockBuffer>();
        private readonly Dictionary<MediaType, int> BlockBufferCounts = new Dictionary<MediaType, int>()
        {
            { MediaType.Video, 12 },
            { MediaType.Audio, 24 },
            { MediaType.Subtitle, 24 }
        };

        private readonly MediaBlockBuffer BlockBuffer;

        private ConfiguredTaskAwaitable PacketReadingTask;
        private readonly CancellationTokenSource PacketReadingCancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim PacketReadingCycle = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim PacketReadingExit = new ManualResetEventSlim(false);

        private ConfiguredTaskAwaitable FrameDecodingTask;


        public PlaybackController(MediaContainer container)
        {
            Container = container;
            foreach (var component in Container.Components.All)
                BlockBuffers[component.MediaType] =
                    new MediaBlockBuffer(BlockBufferCounts[component.MediaType], component.MediaType);

            BlockBuffer = BlockBuffers[Container.Components.Main.MediaType];
        }

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

        public void Test()
        {
            PacketReadingTask = RunPacketReadingTask();
            FrameDecodingTask = RunFrameDecodingTask();

            while (BlockBuffer.Count == 0 ||
                (BlockBuffer.CapacityPercent < 0.5d && Container.IsAtEndOfStream == false))
            {
                PacketReadingCycle.Wait(5);
                AddNextBlock();
            }

            var lastRenderTime = TimeSpan.MinValue;
            var clockPosition = Clock.Position;
            var hasRendered = false;
            var renderIndex = -1;
            MediaBlock renderBlock = null;

            Clock.Position = BlockBuffer.RangeStartTime;
            Clock.Play();

            while (true)
            {

                clockPosition = Clock.Position;
                hasRendered = false;
                renderIndex = BlockBuffer.IndexOf(clockPosition);
                renderBlock = BlockBuffer[renderIndex];

                if (BlockBuffer.IsInRange(clockPosition) == false)
                {
                    $"SYNC ERROR - Setting CLK from {Clock.Position.Debug()} to {BlockBuffer.RangeStartTime.Debug()}".Warn(typeof(MediaContainer));
                    Clock.Position = BlockBuffer.RangeStartTime;
                }

                // render the frame if we have not rendered
                if (renderBlock.StartTime != lastRenderTime)
                {
                    lastRenderTime = renderBlock.StartTime;
                    hasRendered = true;
                    RenderBlock(renderBlock, clockPosition, renderIndex);
                }

                while (hasRendered || renderIndex + 1 > BlockBuffer.Capacity / 2)
                {
                    hasRendered = false;
                    renderIndex = BlockBuffer.IndexOf(clockPosition);
                    AddNextBlock();

                    if (Frames.Count() == 0)
                        break;
                }


                if (Container.IsAtEndOfStream && Container.Components.PacketBufferCount == 0
                    && Frames.Count() == 0 && renderIndex == BlockBuffer.Count - 1)
                {
                    // Rendered all and nothing else to read
                    Clock.Pause();
                    break;
                }

                Thread.Sleep(2);
            }

            $"Finished rendering everything!".Warn(typeof(MediaContainer));

            PacketReadingExit.Wait();

        }

        private void RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            $"BLK: {block.StartTime.Debug()} | CLK: {clockPosition.Debug()} | DFT: {drift.Debug()} | RIX: {renderIndex,6} | FQ: {Frames.Count(),6} | PQ: {Container.Components.PacketBufferLength / 1024d,6:0.00} KB".Info(typeof(MediaContainer));
        }

        /// <summary>
        /// Runs the read task which keeps a packet buffer healthy.
        /// </summary>
        private ConfiguredTaskAwaitable RunPacketReadingTask()
        {
            return Task.Run(() =>
            {
                PacketReadingExit.Reset();

                while (!PacketReadingCancel.IsCancellationRequested)
                {
                    // Enter a read cycle
                    PacketReadingCycle.Reset();

                    // Read a bunch of packets at a time
                    var packetsRead = 0;
                    while (!Container.IsAtEndOfStream
                        && packetsRead < PacketBatchCount
                        && Container.Components.PacketBufferLength < MaxPacketBufferLength)
                    {
                        Container.Read();
                        packetsRead++;
                    }

                    PacketReadingCycle.Set();

                    if (Container.Components.PacketBufferLength > MaxPacketBufferLength || Container.IsAtEndOfStream)
                        Thread.Sleep(1);

                }

                PacketReadingCycle.Set();
                PacketReadingExit.Set();

            }, PacketReadingCancel.Token).ConfigureAwait(false);
        }

        private ConfiguredTaskAwaitable RunFrameDecodingTask()
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

            }).ConfigureAwait(false);
        }
    }
}
