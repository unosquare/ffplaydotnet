namespace Unosquare.FFplayDotNet
{
    using Core;
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Unosquare.Swan;

    public class DecodedFrameList<T>
        where T : Frame, new()
    {
        private readonly Queue<T> FramePool = new Queue<T>();
        private readonly List<T> PlaybackFrames = new List<T>();

        public DecodedFrameList(int capacity)
        {
            Capacity = capacity;
            // allocate the frames
            for (var i = 0; i < capacity; i++)
                FramePool.Enqueue(new T());
        }

        public bool IsFull { get { return FramePool.Count <= 0; } }

        internal string Debug()
        {
            return $"{typeof(T).Name} Frames - Capacity: {Capacity,4} | Pool: {FramePool.Count,4} | Play: {PlaybackFrames.Count,4} | Range: {RangeStartTime.Debug(),8} to {RangeEndTime.Debug()}";
        }

        public void Add(FrameSource source, MediaContainer container)
        {
            // if there are no available frames, make room!
            if (FramePool.Count <= 0)
            {
                var firstFrame = PlaybackFrames[0];
                PlaybackFrames.RemoveAt(0);
                FramePool.Enqueue(firstFrame);
            }

            var targetFrame = FramePool.Dequeue();
            {
                var target = targetFrame as Frame;
                container.Convert(source, ref target, true);
            }

            PlaybackFrames.Add(targetFrame);
            PlaybackFrames.Sort();
        }

        public void Clear()
        {
            // return all the frames to the frame pool
            foreach (var frame in PlaybackFrames)
                FramePool.Enqueue(frame);

            PlaybackFrames.Clear();
        }

        public bool IsInRange(TimeSpan renderTime)
        {
            if (PlaybackFrames.Count == 0) return false;
            return renderTime.Ticks >= RangeStartTime.Ticks && renderTime.Ticks <= RangeEndTime.Ticks;
        }

        public double RangePosition(TimeSpan renderTime)
        {
            if (IsInRange(renderTime) == false) return -1;

            var absRenderTime = renderTime.Ticks - RangeStartTime.Ticks;
            return (double)absRenderTime / RangeDuration.Ticks;
        }

        public int IndexOf(TimeSpan renderTime)
        {
            var frameCount = PlaybackFrames.Count;

            // fast condition checking
            if (frameCount <= 0) return -1;
            if (IsInRange(renderTime) == false) return -1;
            if (frameCount == 1) return 0;

            // variable setup
            var lowIndex = 0;
            var highIndex = frameCount - 1;
            var midIndex = 1 + lowIndex + (highIndex - lowIndex) / 2;

            // edge condition cheching
            if (PlaybackFrames[lowIndex].StartTime >= renderTime) return lowIndex;
            if (PlaybackFrames[highIndex].StartTime <= renderTime) return highIndex;

            // First guess, very low cost, very fast
            if (midIndex < highIndex && renderTime >= PlaybackFrames[midIndex].StartTime && renderTime < PlaybackFrames[midIndex + 1].StartTime)
                return midIndex;

            // binary search
            while (highIndex - lowIndex > 1)
            {
                midIndex = lowIndex + (highIndex - lowIndex) / 2;
                if (renderTime < PlaybackFrames[midIndex].StartTime)
                    highIndex = midIndex;
                else
                    lowIndex = midIndex;
            }

            // linear search
            for (var i = highIndex; i >= lowIndex; i--)
            {
                if (PlaybackFrames[i].StartTime <= renderTime)
                    return i;
            }

            return -1;
        }

        public TimeSpan RangeStartTime { get { return PlaybackFrames.Count == 0 ? TimeSpan.Zero : PlaybackFrames[0].StartTime; } }

        public TimeSpan RangeEndTime
        {
            get
            {
                if (PlaybackFrames.Count == 0) return TimeSpan.Zero;
                var lastFrame = PlaybackFrames[PlaybackFrames.Count - 1];
                return TimeSpan.FromTicks(lastFrame.StartTime.Ticks + lastFrame.Duration.Ticks);
            }
        }

        public TimeSpan RangeDuration { get { return TimeSpan.FromTicks(RangeEndTime.Ticks - RangeStartTime.Ticks); } }

        public T this[int index]
        {
            get { return PlaybackFrames[index]; }
        }

        public int Count { get { return PlaybackFrames.Count; } }

        public int Capacity { get; private set; }

        public double CapacityPercent { get { return (double)Count / Capacity; } }
    }

    public class PlaybackManager
    {
        private const int MaxPacketQueueSize = 24;

        private readonly DecodedFrameList<VideoFrame> VideoFrames = new DecodedFrameList<VideoFrame>(25);
        //private readonly FrameSourceQueue
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
            while (Clock.Position.TotalSeconds < Container.MediaDuration.TotalSeconds)
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

        private int DecodeAddFrames()
        {
            var addedFrames = 0;

            while (Container.Components.PacketBufferCount > 0 && addedFrames <= 0)
            {
                ReadTaskCycleDone.Wait(1);
                var sources = Container.Decode();
                foreach (var source in sources)
                {
                    if (source.MediaType == MediaType.Video)
                    {
                        VideoFrames.Add(source, Container);
                        VideoFrames.Debug().Trace(typeof(MediaContainer));
                        addedFrames += 1;
                    }
                    else
                        source.Dispose();
                }
            }

            return addedFrames;

        }

        private void BufferFrames()
        {
            // Wait for enough packets to arrive
            while (CanReadMorePackets)
                ReadTaskCycleDone.Wait();

            // Fill some frames until we are in range
            while (VideoFrames.CapacityPercent < 0.5d && Container.IsAtEndOfStream == false)
            {
                // Wait fro packets if we have drained them all
                while (Container.Components.PacketBufferCount <= 0)
                    ReadTaskCycleDone.Wait(1);

                DecodeAddFrames();
            }

            if (VideoFrames.Count <= 0) throw new MediaContainerException("Buffering of frames produced no results!");

            $"Buffered {VideoFrames.Count} Frames".Info(typeof(MediaContainer));

            Clock.Reset();
            Clock.Position = VideoFrames.RangeStartTime;
            Clock.Play();
        }

        private void RenderFrame(Frame frame)
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

                    // Check if we have a valid render index
                    var renderIndex = VideoFrames.IndexOf(clockPosition);
                    if (renderIndex < 0)
                    {
                        if (VideoFrames.Count > 0)
                        {
                            Clock.Reset();
                            Clock.Position = VideoFrames.RangeStartTime;
                            $"Clock position {clockPosition.Debug()} was not available. Clock was updated to {Clock.Position.Debug()}".Error(typeof(MediaContainer));
                            clockPosition = Clock.Position;
                            renderIndex = 0;
                            Clock.Play();
                        }
                        else
                        {
                            BufferFrames();
                            continue;
                        }
                    }

                    var frame = VideoFrames[renderIndex];
                    var rendered = false;
                    // Check if we need to render
                    if (lastFrameTime != frame.StartTime)
                    {
                        lastFrameTime = frame.StartTime;
                        $"Render - Clock: {clockPosition.Debug(),12} | Frame: {frame.StartTime.Debug(),12} | Index: {renderIndex}".Warn(typeof(MediaContainer));
                        RenderFrame(frame);
                        rendered = true;
                        // We just rendered the frame, make room for a new one
                        //DecodeAddFrame();
                        //continue;
                    }

                    // We neeed to decode a new frame if:
                    // we have rendered a frame or we are running low
                    // AND if there are packets to decode

                    var needsMoreFrames = true;
                    while (needsMoreFrames)
                    {
                        ReadTaskCycleDone.Wait(10);
                        needsMoreFrames = (rendered || renderIndex > (VideoFrames.Count / 2)) && Container.Components.PacketBufferCount > 0;
                        if (!needsMoreFrames)
                            break;
                        renderIndex = VideoFrames.IndexOf(clockPosition);
                        rendered = false;
                        
                        var addedFrames = DecodeAddFrames();
                        $"DEC {addedFrames}".Info();
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
