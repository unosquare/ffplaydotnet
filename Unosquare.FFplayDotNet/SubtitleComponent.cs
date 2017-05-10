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

        protected override unsafe Frame CreateFrame(AVSubtitle* frame)
        {
            var frameHolder = new SubtitleFrame(frame, Stream->time_base);
            return frameHolder;
        }

        protected override void DecompressFrame(Frame genericFrame)
        {
            var frame = genericFrame as SubtitleFrame;

            // Check if there is a handler to feed the conversion to.
            if (Container.HandlesOnSubtitleDataAvailable == false)
                return;

            // Extract text strings
            var subtitleText = new List<string>();

            for (var i = 0; i < frame.Pointer->num_rects; i++)
            {
                var rect = frame.Pointer->rects[i];
                if (rect->text != null)
                    subtitleText.Add(Utils.PtrToStringUTF8(rect->text));

            }

            // Provide the data in an event
            Container.RaiseOnSubtitleDataAvailabe(subtitleText.ToArray(), frame.StartTime, frame.EndTime, frame.Duration);
        }
    }

}
