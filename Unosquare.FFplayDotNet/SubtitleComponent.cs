namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using Unosquare.FFplayDotNet.Core;

    /// <summary>
    /// Performs subtitle text decoding and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class SubtitleComponent : MediaComponent
    {
        internal SubtitleComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            // placeholder. Nothing else to change here.
        }

        protected override unsafe FrameSource CreateFrame(AVSubtitle* frame)
        {
            var frameHolder = new SubtitleFrameSource(frame, Stream->time_base);
            return frameHolder;
        }

        internal override void Materialize(FrameSource input, Frame output)
        {
            var source = input as SubtitleFrameSource;
            var target = output as SubtitleFrame;

            if (source == null || target == null)
                throw new ArgumentNullException($"{nameof(input)} and {nameof(output)} are either null or not of a compatible media type '{MediaType}'");

            // Set the target data
            target.Duration = source.Duration;
            target.EndTime = source.EndTime;
            target.StartTime = source.StartTime;
            target.Text.Clear();
            target.Text.AddRange(source.Text);
        }
    }

}
