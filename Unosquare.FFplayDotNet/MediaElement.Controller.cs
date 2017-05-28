namespace Unosquare.FFplayDotNet
{
    using Core;
    using Decoding;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    partial class MediaElement
    {

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

        #region Private Members

        private MediaContainer Container = null;
        private readonly Clock Clock = new Clock();

        private readonly Dictionary<MediaType, MediaFrameQueue> Frames
            = new Dictionary<MediaType, MediaFrameQueue>(StateDictionaryCapacity);

        private readonly Dictionary<MediaType, MediaBlockBuffer> Blocks
            = new Dictionary<MediaType, MediaBlockBuffer>(StateDictionaryCapacity);

        private readonly Dictionary<MediaType, TimeSpan> LastRenderTime
            = new Dictionary<MediaType, TimeSpan>(StateDictionaryCapacity);

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
        private TimeSpan? RequestedSeekPosition = null;

        #endregion

        #region Private Methods

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
        private void AddNextBlock(MediaType t)
        {
            var frame = Frames[t].Dequeue();
            if (frame == null) return;
            Blocks[t].Add(frame, Container);
        }

        /// <summary>
        /// Buffers some packets which in turn get decoded into frames and then
        /// converted into blocks.
        /// </summary>
        /// <param name="packetBufferLength">Length of the packet buffer.</param>
        /// <param name="setClock">if set to <c>true</c> [set clock].</param>
        private async Task BufferBlocks(int packetBufferLength, bool setClock)
        {
            // Raise the buffering started event.
            await RaiseBufferingStartedEvent();
            BufferingProgress = 0;

            // Pause the clock while we buffer blocks
            var wasClockRunning = Clock.IsRunning;
            if (setClock) Clock.Pause();

            // Buffer some packets
            while (CanReadMorePackets && Container.Components.PacketBufferLength < packetBufferLength)
                PacketReadingCycle.Wait(1);

            // Wait up to 1 second to decode frames. This happens much faster but 1s is plenty.
            FrameDecodingCycle.Wait(1000);

            // Buffer some blocks
            while (CanReadMoreBlocks && Blocks.All(b => b.Value.CapacityPercent < 0.5d))
            {
                PacketReadingCycle.Wait(1);
                FrameDecodingCycle.Wait(1);
                BufferingProgress = Blocks.Average(b => b.Value.CapacityPercent);
                foreach (var t in Container.Components.MediaTypes)
                    AddNextBlock(t);
            }

            // Resume and set the clock if requested.
            if (setClock)
            {
                Clock.Position = Blocks[Container.Components.Main.MediaType].RangeStartTime;
                if (wasClockRunning) Clock.Play();
            }

            // Raise the buffering started event.
            BufferingProgress = 1;
            await RaiseBufferingEndedEvent();

        }

        /// <summary>
        /// The render block callback that updates the reported media position
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <param name="renderIndex">Index of the render.</param>
        private async Task RenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            if (block.MediaType == MediaType.Video)
                await InvokeAction(() =>
                {
                    var e = block as VideoBlock;
                    TargetBitmap.Lock();

                    if (TargetBitmap.BackBufferStride != e.BufferStride)
                    {
                        var sourceBase = e.Buffer;
                        var targetBase = TargetBitmap.BackBuffer;

                        for (var y = 0; y < TargetBitmap.PixelHeight; y++)
                        {
                            var sourceAddress = sourceBase + (e.BufferStride * y);
                            var targetAddress = targetBase + (TargetBitmap.BackBufferStride * y);
                            Utils.CopyMemory(targetAddress, sourceAddress, (uint)e.BufferStride);
                        }
                    }
                    else
                    {
                        Utils.CopyMemory(TargetBitmap.BackBuffer, e.Buffer, (uint)e.BufferLength);
                    }

                    TargetBitmap.AddDirtyRect(new Int32Rect(0, 0, e.PixelWidth, e.PixelHeight));
                    TargetBitmap.Unlock();
                });

            var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
            Container.Log(MediaLogMessageType.Trace,
            ($"{block.MediaType.ToString().Substring(0, 1)} "
                + $"BLK: {block.StartTime.Debug()} | "
                + $"CLK: {clockPosition.Debug()} | "
                + $"DFT: {drift.TotalMilliseconds,4:0} | "
                + $"IX: {renderIndex,3} | "
                + $"FQ: {Frames[block.MediaType].Count,4} | "
                + $"PQ: {Container.Components[block.MediaType].PacketBufferLength / 1024d,7:0.0}k | "
                + $"TQ: {Container.Components.PacketBufferLength / 1024d,7:0.0}k"));
        }

        /// <summary>
        /// Performs a seek operation to the specified position.
        /// </summary>
        /// <param name="position">The position.</param>
        private void Seek(TimeSpan position)
        {
            SeekingDone.Wait();
            var startTime = DateTime.UtcNow;
            var resumeClock = Clock.IsRunning;
            Clock.Pause();

            SeekingDone.Reset();
            PacketReadingCycle.Wait();
            FrameDecodingCycle.Wait();
            BlockRenderingCycle.Wait();

            // Clear Blocks and frames, reset the render times
            foreach (var t in Container.Components.MediaTypes)
            {
                Frames[t].Clear();
                Blocks[t].Clear();
                LastRenderTime[t] = TimeSpan.MinValue;
            }

            // Populate frame with after-seek operation
            var frames = Container.Seek(position);
            foreach (var frame in frames)
                Frames[frame.MediaType].Push(frame);

            // Resume the clock if it was running before the seek operation
            OnPropertyChanged(nameof(Position));
            if (resumeClock)
                Clock.Play();

            Container.Log(MediaLogMessageType.Debug,
                $"SEEK D: Elapsed: {startTime.DebugElapsedUtc()}");

            RequestedSeekPosition = null;
            SeekingDone.Set();
        }

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

        #region Public API

        private async Task Open(Uri uri)
        {
            try
            {
                var mediaUrl = uri.IsFile ? uri.LocalPath : uri.ToString();

                Container = new MediaContainer(mediaUrl);
                await RaiseMediaOpeningEvent();
                Container.Initialize();

                if (HasVideo)
                    TargetBitmap = new WriteableBitmap(NaturalVideoWidth, NaturalVideoHeight, 96, 96, PixelFormats.Bgr24, null);
                else
                    TargetBitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgr24, null);

                ViewBox.Source = TargetBitmap;

                foreach (var t in Container.Components.MediaTypes)
                {
                    Blocks[t] = new MediaBlockBuffer(MaxBlocks[t], t);
                    Frames[t] = new MediaFrameQueue();
                    LastRenderTime[t] = TimeSpan.MinValue;
                }

                PacketReadingTask = Task.Run(() => { RunPacketReadingTask(); }, PacketReadingCancel.Token);
                FrameDecodingTask = Task.Run(() => { RunFrameDecodingTask(); }, FrameDecodingCancel.Token);
                BlockRenderingTask = Task.Run(async () => { await RunBlockRenderingTask(); }, BlockRenderingCancel.Token);
                await RaiseMediaOpenedEvent();
            }
            catch (Exception ex)
            {
                await RaiseMediaFailedEvent(ex);
            }
            finally
            {
                UpdateMediaProperties();
            }
        }

        public void Play()
        {
            Clock.Play();
            BlockRenderingCycle.Wait(5);
            MediaState = MediaState.Play;
        }

        public void Pause()
        {

        }

        public void Stop()
        {

        }

        public void Close()
        {

            // TODO: Cancel and stop the tasks.

            // Reset the clock
            Clock.Reset();

            // Dispose the container
            if (Container != null)
            {
                Container.Dispose();
                Container = null;
            }

            // Dispose the Blocks for all components
            foreach (var kvp in Blocks) kvp.Value.Dispose();
            Blocks.Clear();

            // Dispose the Frames for all components
            foreach (var kvp in Frames) kvp.Value.Dispose();
            Frames.Clear();

            // Clear the render times
            LastRenderTime.Clear();

            // Update notification properties
            UpdateMediaProperties();
        }

        #endregion

        #region Task Runners

        /// <summary>
        /// Runs the read task which keeps a packet buffer as full as possible.
        /// </summary>
        private void RunPacketReadingTask()
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
        }

        /// <summary>
        /// Continually decodes the available packet buffer to have as
        /// many frames as possible in each frame queue and 
        /// up to the MaxFrames on each component
        /// </summary>
        private void RunFrameDecodingTask()
        {
            while (FrameDecodingCancel.IsCancellationRequested == false)
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
        /// <returns></returns>
        private async Task RunBlockRenderingTask()
        {
            var mediaTypeCount = Container.Components.MediaTypes.Length;
            var main = Container.Components.Main.MediaType;

            var clockPosition = Clock.Position;

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
            await BufferBlocks(WaitPacketBufferLength, true);

            while (BlockRenderingCancel.IsCancellationRequested == false)
            {
                if (RequestedSeekPosition != null)
                    Seek(RequestedSeekPosition.Value);

                SeekingDone.Wait();
                BlockRenderingCycle.Reset();

                // Capture current time and render index
                OnPropertyChanged(nameof(Position));
                clockPosition = Clock.Position;
                renderIndex[main] = Blocks[main].IndexOf(clockPosition);

                // Check for out-of sync issues (i.e. after seeking)
                if (Blocks[main].IsInRange(clockPosition) == false || renderIndex[main] < 0)
                {
                    await BufferBlocks(WaitPacketBufferLength, true);
                    Container.Log(MediaLogMessageType.Warning,
                        $"SYNC              CLK: {clockPosition.Debug()} | TGT: {Blocks[main].RangeStartTime.Debug()}");
                    clockPosition = Clock.Position;
                    renderIndex[main] = Blocks[main].IndexOf(clockPosition);
                }

                foreach (var t in Container.Components.MediaTypes)
                {
                    var blocks = Blocks[t];
                    renderIndex[t] = blocks.IndexOf(clockPosition);
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
                        await RenderBlock(renderBlock[t], clockPosition, renderIndex[t]);
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
                    if (MediaState != MediaState.Pause)
                    {
                        // Rendered all and nothing else to read
                        Clock.Pause();
                        Clock.Position = Blocks[main].RangeEndTime;
                        MediaState = MediaState.Pause;
                        OnPropertyChanged(nameof(Position));
                        await RaiseMediaEndedEvent();
                    }

                }

                BlockRenderingCycle.Set();
                Thread.Sleep(1);
            }

            BlockRenderingCycle.Set();
        }

        #endregion

    }
}
