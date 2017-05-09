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

        /// <summary>
        /// Processes the subtitle frame. Only text subtitles are supported.
        /// </summary>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVSubtitle* frame)
        {
            // Extract timing information
            var renderTime = frame->pts.ToTimeSpan();
            var startTime = renderTime + ((long)frame->start_display_time).ToTimeSpan(Stream->time_base);
            var endTime = renderTime + ((long)frame->end_display_time).ToTimeSpan(Stream->time_base);
            var duration = endTime - startTime;

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastFrameRenderTime = renderTime;

            // Check if there is a handler to feed the conversion to.
            if (Container.HandlesOnSubtitleDataAvailable == false)
                return;

            // Extract text strings
            var subtitleText = new List<string>();

            for (var i = 0; i < frame->num_rects; i++)
            {
                var rect = frame->rects[i];
                if (rect->text != null)
                    subtitleText.Add(Utils.PtrToStringUTF8(rect->text));

            }

            // Provide the data in an event
            Container.RaiseOnSubtitleDataAvailabe(subtitleText.ToArray(), startTime, endTime, duration);
        }
    }

}
