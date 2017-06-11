namespace Unosquare.FFplayDotNet.Commands
{
    using System;
    using Unosquare.FFplayDotNet.Core;

    internal sealed class SeekCommand : MediaCommand
    {
        public TimeSpan TargetPosition = TimeSpan.Zero;

        public SeekCommand(MediaElement mediaElement, TimeSpan targetPosition)
            : base(mediaElement, MediaCommandType.Seek)
        {
            TargetPosition = targetPosition;
        }

        protected override void Execute()
        {
            var m = MediaElement;

            m.SeekingDone.Reset();
            var startTime = DateTime.UtcNow;
            var resumeClock = m.Clock.IsRunning;
            m.Clock.Pause();

            m.PacketReadingCycle.Wait();
            m.FrameDecodingCycle.Wait();

            // Clear Blocks and frames, reset the render times
            foreach (var t in m.Container.Components.MediaTypes)
            {
                m.Frames[t].Clear();
                m.Blocks[t].Clear();
                m.LastRenderTime[t] = TimeSpan.MinValue;
            }

            // Populate frame with after-seek operation
            var frames = m.Container.Seek(TargetPosition);
            foreach (var frame in frames)
                m.Frames[frame.MediaType].Push(frame);

            // Resume the clock if it was running before the seek operation
            if (resumeClock)
                m.Clock.Play();

            m.Container.Log(MediaLogMessageType.Debug,
                $"SEEK D: Elapsed: {startTime.DebugElapsedUtc()}");

            m.SeekingDone.Set();
        }
    }
}
