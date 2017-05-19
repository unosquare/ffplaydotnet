namespace Unosquare.FFplayDotNet
{
    using Core;
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Unosquare.Swan;

    public class PlaybackManager
    {
        private const int MaxPacketQueueSize = 48;

        private readonly MediaBlockBuffer VideoBlocks = new MediaBlockBuffer(100, MediaType.Video);
        private readonly MediaFrameQueue VideoFrames = new MediaFrameQueue();

        //private readonly DecodedFrameList<AudioFrame> AudioFrames = new DecodedFrameList<AudioFrame>(60);
        //private readonly DecodedFrameList<SubtitleFrame> SubtitleFrames = new DecodedFrameList<SubtitleFrame>(4);

        private readonly Clock Clock = new Clock();

        private MediaContainer Container;

        private ConfiguredTaskAwaitable ReadTask;
        private readonly ManualResetEventSlim ReadTaskCycleDone = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim ReadTaskDone = new ManualResetEventSlim(false);
        private readonly CancellationTokenSource ReadTaskCancel = new CancellationTokenSource();

        private ConfiguredTaskAwaitable DecodeTask;
        private readonly CancellationTokenSource DecodeTaskCancel = new CancellationTokenSource();

        private readonly ManualResetEventSlim SeekOperationDone = new ManualResetEventSlim(true);

        public PlaybackManager(MediaContainer container)
        {
            Container = container;

        }

        public void Test()
        {
            //var c = new Clock();

            //c.Play();
            //while (c.Position.TotalSeconds < 10)
            //{
            //    $"{c.Position.Debug()}".Warn();
            //    Thread.Sleep(1000);
            //}
            //return;


            ReadTask = RunReadTask();
            DecodeTask = RunDecodeTask();

            var startTime = DateTime.Now;
            while (Clock.Position.TotalSeconds < 60)
            {
                Thread.Sleep(1);
            }

            $"Task Finished in {DateTime.Now.Subtract(startTime).Debug()}".Info();

            DecodeTaskCancel.Cancel(false);
            ReadTaskCancel.Cancel(false);

            DecodeTask.GetAwaiter().GetResult();
            ReadTask.GetAwaiter().GetResult();
        }


        private bool CanReadMorePackets
        {
            get
            {
                return Container.IsAtEndOfStream == false
                  && ReadTaskCancel.IsCancellationRequested == false
                  && Container.Components.PacketBufferCount < MaxPacketQueueSize;
            }
        }

        private int DecodeAddNextFrame()
        {
            var dequeuedFrames = 0;
            var addedFrames = 0;

            if (VideoFrames.Count > 0)
            {
                VideoBlocks.Add(VideoFrames.Dequeue(), Container);
                VideoBlocks.Debug().Trace(typeof(MediaContainer));
                dequeuedFrames += 1;
                //return addedFrames;
            }

            while (Container.Components.PacketBufferCount > 0 && addedFrames <= 0)
            {
                ReadTaskCycleDone.Wait(1);
                var sources = Container.Decode();
                foreach (var source in sources)
                {
                    if (source.MediaType == MediaType.Video)
                    {
                        VideoFrames.Push(source);
                        addedFrames += 1;
                    }
                    else
                        source.Dispose();
                }
            }

            if (dequeuedFrames <= 0 && VideoFrames.Count > 0)
            {
                VideoBlocks.Add(VideoFrames.Dequeue(), Container);
                VideoBlocks.Debug().Trace(typeof(MediaContainer));
                dequeuedFrames += 1;
            }

            return addedFrames;

        }

        private void BufferFrames()
        {
            // Wait for enough packets to arrive
            while (CanReadMorePackets)
                ReadTaskCycleDone.Wait();

            // Fill some frames until we are in range
            while (VideoBlocks.CapacityPercent < 0.5d && Container.IsAtEndOfStream == false)
            {
                // Wait for packets if we have drained them all
                while (Container.Components.PacketBufferCount <= 0)
                    ReadTaskCycleDone.Wait(1);

                DecodeAddNextFrame();
            }

            if (VideoBlocks.Count <= 0) throw new MediaContainerException("Buffering of frames produced no results!");

            $"Buffered {VideoBlocks.Count} Frames".Info(typeof(MediaContainer));

            Clock.Reset();
            Clock.Position = VideoBlocks.RangeStartTime;
            Clock.Play();
        }

        private void RenderFrame(MediaBlock frame)
        {
            //$"Render Frame {frame.StartTime.Debug()} called".Info(typeof(MediaContainer));
        }

        private ConfiguredTaskAwaitable RunDecodeTask()
        {

            return Task.Run(() =>
            {
                var clockPosition = Clock.Position;
                var lastFrameTime = TimeSpan.MinValue;

                BufferFrames();

                while (!DecodeTaskCancel.IsCancellationRequested)
                {
                    clockPosition = Clock.Position;
                    var renderIndex = 0;

                    if (VideoBlocks.IsInRange(clockPosition) == false)
                    {
                        $"ERROR - No frame at {clockPosition}. Available Packets: {Container.Components.PacketBufferCount}, Queued Sources: {VideoFrames.Count}".Error();
                        //if (clockPosition > VideoFrames.RangeEndTime)

                        //if (VideoFrames.Count > 0)
                        //{
                        //    Clock.Reset();
                        //    Clock.Position = VideoFrames.RangeStartTime;
                        //    $"SYNC - Missing Frame at {clockPosition.Debug()} | Source Queue: {VideoSources.Count} | New Clock: {Clock.Position.Debug()}".Error(typeof(MediaContainer));
                        //    clockPosition = Clock.Position;
                        //    Clock.Play();
                        //}
                        //else
                        //{
                        //    BufferFrames();
                        //    continue;
                        //}
                    }

                    // Retrieve the frame to render
                    renderIndex = VideoBlocks.IndexOf(clockPosition);
                    var frame = VideoBlocks[renderIndex];
                    var rendered = false;
                    // Check if we need to render
                    if (lastFrameTime != frame.StartTime)
                    {
                        lastFrameTime = frame.StartTime;
                        $"{"Render",-12} - CLK: {clockPosition.Debug(),8} | IX: {renderIndex,8} | QUE: {VideoFrames.Count,4} | FRM: {frame.StartTime.Debug(),8} to {frame.EndTime.Debug().Trim()}".Warn(typeof(MediaContainer));
                        RenderFrame(frame);
                        rendered = true;
                    }

                    // Check if we have reached the end of the stream
                    if (rendered == true && Container.Components.PacketBufferCount <= 0 && Container.IsAtEndOfStream && VideoFrames.Count == 0 && renderIndex == VideoBlocks.Count - 1)
                    {
                        // Pause for the duration of the last frame
                        Thread.Sleep(frame.Duration);
                        Clock.Pause();
                        $"End of stream reached at clock position: {Clock.Position.Debug()}".Info();
                        Clock.Play();
                        return;
                    }

                    // We neeed to decode a new frame if:
                    // we have rendered a frame or we are running low
                    // AND if there are packets to decode

                    var needsMoreFrames = true;
                    while (needsMoreFrames)
                    {
                        ReadTaskCycleDone.Wait(10);
                        needsMoreFrames = (rendered || renderIndex > (VideoBlocks.Count / 2));

                        if (!needsMoreFrames)
                            break;

                        renderIndex = VideoBlocks.IndexOf(clockPosition);
                        rendered = false;

                        if (Container.Components.PacketBufferCount <= 0)
                            ReadTaskCycleDone.Wait(10);

                        var addedFrames = DecodeAddNextFrame();

                        if (Container.Components.PacketBufferCount <= 0 && Container.IsAtEndOfStream)
                            break;

                        //$"DEC {addedFrames}".Info();
                    }

                    if (!DecodeTaskCancel.IsCancellationRequested)
                        Thread.Sleep(1);
                }
            }, DecodeTaskCancel.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the read task which keeps a packet buffer healthy.
        /// </summary>
        private ConfiguredTaskAwaitable RunReadTask()
        {
            return Task.Run(() =>
            {
                ReadTaskDone.Reset();

                while (!ReadTaskCancel.IsCancellationRequested)
                {
                    // Enter a read cycle
                    ReadTaskCycleDone.Reset();

                    // If we are at the end of the stream or the buffer is full, then we need to pause a bit.
                    if (CanReadMorePackets == false)
                    {
                        ReadTaskCycleDone.Set();
                        Thread.Sleep(2);
                        continue;
                    }

                    try
                    {
                        while (CanReadMorePackets)
                            Container.Read();
                    }
                    catch { }
                    finally
                    {
                        ReadTaskCycleDone.Set();
                    }

                }

                ReadTaskCycleDone.Set();
                ReadTaskDone.Set();

            }, ReadTaskCancel.Token).ConfigureAwait(false);
        }


    }
}
