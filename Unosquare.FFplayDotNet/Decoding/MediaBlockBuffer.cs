namespace Unosquare.FFplayDotNet.Decoding
{
    using Core;
    using System;
    using System.Collections.Generic;

    public class MediaBlockBuffer
    {
        private readonly Queue<MediaBlock> PoolBlocks = new Queue<MediaBlock>();
        private readonly List<MediaBlock> PlaybackBlocks = new List<MediaBlock>();

        public MediaBlockBuffer(int capacity, MediaType mediaType)
        {
            Capacity = capacity;
            MediaType = mediaType;

            // allocate the frames
            for (var i = 0; i < capacity; i++)
                PoolBlocks.Enqueue(CreateBlock());
        }

        private MediaBlock CreateBlock()
        {
            if (MediaType == MediaType.Video) return new VideoBlock();
            if (MediaType == MediaType.Audio) return new AudioBlock();
            if (MediaType == MediaType.Subtitle) return new SubtitleBlock();

            throw new InvalidCastException($"No {nameof(MediaBlock)} constructor for {nameof(MediaType)} '{MediaType}'");
        }

        public bool IsFull { get { return PoolBlocks.Count <= 0; } }

        public MediaType MediaType { get; private set; }

        internal string Debug()
        {
            return $"{MediaType,-12} - CAP: {Capacity,10} | FRE: {PoolBlocks.Count,7} | USD: {PlaybackBlocks.Count,4} |  TM: {RangeStartTime.Debug(),8} to {RangeEndTime.Debug().Trim()}";
        }

        public void Add(MediaFrame source, MediaContainer container)
        {
            // if there are no available frames, make room!
            if (PoolBlocks.Count <= 0)
            {
                var firstFrame = PlaybackBlocks[0];
                PlaybackBlocks.RemoveAt(0);
                PoolBlocks.Enqueue(firstFrame);
            }

            var targetFrame = PoolBlocks.Dequeue();
            {
                var target = targetFrame as MediaBlock;
                container.Convert(source, ref target, true);
            }

            PlaybackBlocks.Add(targetFrame);
            PlaybackBlocks.Sort();
        }

        public void Clear()
        {
            // return all the frames to the frame pool
            foreach (var frame in PlaybackBlocks)
                PoolBlocks.Enqueue(frame);

            PlaybackBlocks.Clear();
        }

        public bool IsInRange(TimeSpan renderTime)
        {
            if (PlaybackBlocks.Count == 0) return false;
            return renderTime.Ticks >= RangeStartTime.Ticks && renderTime.Ticks <= RangeEndTime.Ticks;
        }

        public int IndexOf(TimeSpan renderTime)
        {
            var frameCount = PlaybackBlocks.Count;

            // fast condition checking
            if (frameCount <= 0) return -1;
            if (frameCount == 1) return 0;

            // variable setup
            var lowIndex = 0;
            var highIndex = frameCount - 1;
            var midIndex = 1 + lowIndex + (highIndex - lowIndex) / 2;

            // edge condition cheching
            if (PlaybackBlocks[lowIndex].StartTime >= renderTime) return lowIndex;
            if (PlaybackBlocks[highIndex].StartTime <= renderTime) return highIndex;

            // First guess, very low cost, very fast
            if (midIndex < highIndex && renderTime >= PlaybackBlocks[midIndex].StartTime && renderTime < PlaybackBlocks[midIndex + 1].StartTime)
                return midIndex;

            // binary search
            while (highIndex - lowIndex > 1)
            {
                midIndex = lowIndex + (highIndex - lowIndex) / 2;
                if (renderTime < PlaybackBlocks[midIndex].StartTime)
                    highIndex = midIndex;
                else
                    lowIndex = midIndex;
            }

            // linear search
            for (var i = highIndex; i >= lowIndex; i--)
            {
                if (PlaybackBlocks[i].StartTime <= renderTime)
                    return i;
            }

            return -1;
        }

        public TimeSpan RangeStartTime { get { return PlaybackBlocks.Count == 0 ? TimeSpan.Zero : PlaybackBlocks[0].StartTime; } }

        public TimeSpan RangeEndTime
        {
            get
            {
                if (PlaybackBlocks.Count == 0) return TimeSpan.Zero;
                var lastFrame = PlaybackBlocks[PlaybackBlocks.Count - 1];
                return TimeSpan.FromTicks(lastFrame.StartTime.Ticks + lastFrame.Duration.Ticks);
            }
        }

        public TimeSpan RangeDuration { get { return TimeSpan.FromTicks(RangeEndTime.Ticks - RangeStartTime.Ticks); } }

        public MediaBlock this[int index]
        {
            get { return PlaybackBlocks[index]; }
        }

        public int Count { get { return PlaybackBlocks.Count; } }

        public int Capacity { get; private set; }

        public double CapacityPercent { get { return (double)Count / Capacity; } }
    }

}
