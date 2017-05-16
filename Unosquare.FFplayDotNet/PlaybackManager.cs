namespace Unosquare.FFplayDotNet
{
    using Core;
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
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
            return $"{typeof(T).Name} Frames - Capacity: {Capacity,4} | Pool: {FramePool.Count,4} | Play: {PlaybackFrames.Count,4} | Range: {RangeStartTime.Debug(),8} to {RangeEndTime.Debug(),8}";
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

        public int IndexOf(TimeSpan renderTime)
        {
            var frameCount = PlaybackFrames.Count;

            // fast condition checking
            if (frameCount <= 0) return -1;
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

    }

    public class PlaybackManager
    {
        private const int MaxPacketQueueSize = 24;

        private readonly DecodedFrameList<VideoFrame> VideoFrames = new DecodedFrameList<VideoFrame>(25);
        private readonly DecodedFrameList<AudioFrame> AudioFrames = new DecodedFrameList<AudioFrame>(60);
        private readonly DecodedFrameList<SubtitleFrame> SubtitleFrames = new DecodedFrameList<SubtitleFrame>(4);

        private readonly Clock Clock = new Clock();

        private MediaContainer Container;

        private ConfiguredTaskAwaitable ReadTask;
        private readonly ManualResetEventSlim ReadTaskCycleDone = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim ReadTaskDone = new ManualResetEventSlim(false);
        private readonly CancellationTokenSource ReadTaskCancel = new CancellationTokenSource();

        private readonly ManualResetEventSlim SeekOperationDone = new ManualResetEventSlim(true);

        public PlaybackManager(MediaContainer container)
        {
            Container = container;
            //ReadTask = RunReadTask();
        }

        public void Test()
        {
            ReadTask = RunReadTask();

            var clock = new Clock();
            var lastRenderTime = TimeSpan.Zero;

            while (Container.IsAtEndOfStream == false || Container.Components.PacketBufferCount > 0)
            {
                var sources = Container.Decode();
                foreach (var source in sources)
                {
                    if (source.MediaType == MediaType.Video)
                    {
                        VideoFrames.Add(source, Container);
                        VideoFrames.Debug().Trace(typeof(MediaContainer));
                    }
                    else if (source.MediaType == MediaType.Audio)
                    {
                        AudioFrames.Add(source, Container);
                        AudioFrames.Debug().Trace(typeof(MediaContainer));
                    }
                    else if (source.MediaType == MediaType.Subtitle)
                    {
                        SubtitleFrames.Add(source, Container);
                        SubtitleFrames.Debug().Trace(typeof(MediaContainer));
                    }
                    else
                        source.Dispose();
                }

                if (clock.IsRunning == false && VideoFrames.Count > 6)
                {
                    clock.Position = VideoFrames[0].StartTime;
                    $"Clock started at {clock.Position.Debug()}".Trace(typeof(MediaContainer));
                    clock.Play();
                }

                var clockPosition = clock.Position;

                var renderIndex = VideoFrames.IndexOf(clockPosition);
                if (renderIndex < 0) continue;

                var frame = VideoFrames[renderIndex];
                $"Render - Clock: {clockPosition.Debug(),12} | Frame: {frame.StartTime.Debug(),12} | Index: {renderIndex}".Warn(typeof(MediaContainer));

                var timeDifference = TimeSpan.FromTicks(clockPosition.Ticks - lastRenderTime.Ticks);
                if (timeDifference.TotalMilliseconds < 40)
                    Thread.Sleep(40 - (int)timeDifference.TotalMilliseconds);

            }

        }

        public void Play()
        {

        }

        private bool IsPacketQueueFull
        {
            get { return Container.Components.PacketBufferCount >= MaxPacketQueueSize; }
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
                    if (Container.IsAtEndOfStream || IsPacketQueueFull)
                    {
                        ReadTaskCycleDone.Set();
                        if (!ReadTaskCancel.IsCancellationRequested)
                            Thread.Sleep(1);

                        continue;
                    }

                    try
                    {
                        while (!IsPacketQueueFull && !Container.IsAtEndOfStream && !ReadTaskCancel.IsCancellationRequested)
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
