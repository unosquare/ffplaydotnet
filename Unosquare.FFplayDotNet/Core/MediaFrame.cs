namespace Unosquare.FFplayDotNet.Core
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using Unosquare.FFplayDotNet.Decoding;

    /// <summary>
    /// Represents a wrapper for an unmanaged frame.
    /// Derived classes implement the specifics of each media type.
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe abstract class MediaFrame : IDisposable, IComparable<MediaFrame>
    {

        #region Private Members

        protected void* InternalPointer;
        private bool IsDisposed = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaFrame" /> class.
        /// </summary>
        /// <param name="pointer">The pointer.</param>
        /// <param name="component">The component.</param>
        internal MediaFrame(void* pointer, MediaComponent component)
        {
            InternalPointer = pointer;
            StreamTimeBase = component.Stream->time_base;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        /// <value>
        /// The type of the media.
        /// </value>
        public abstract MediaType MediaType { get; }

        /// <summary>
        /// Gets the start time of the frame.
        /// </summary>
        public TimeSpan StartTime { get; protected set; }

        /// <summary>
        /// Gets the end time of the frame
        /// </summary>
        public TimeSpan EndTime { get; protected set; }

        /// <summary>
        /// Gets the time base of the stream that generated this frame.
        /// </summary>
        internal AVRational StreamTimeBase { get; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; protected set; }

        /// <summary>
        /// When the unmanaged frame is released (freed from unmanaged memory)
        /// this property will return true.
        /// </summary>
        public bool IsStale { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Releases internal frame 
        /// </summary>
        protected abstract void Release();

        /// <summary>
        /// Compares the current instance with another object of the same type and returns an integer that indicates whether the current instance precedes, follows, or occurs in the same position in the sort order as the other object.
        /// </summary>
        /// <param name="other">An object to compare with this instance.</param>
        /// <returns>
        /// A value that indicates the relative order of the objects being compared. The return value has these meanings: Value Meaning Less than zero This instance precedes <paramref name="other" /> in the sort order.  Zero This instance occurs in the same position in the sort order as <paramref name="other" />. Greater than zero This instance follows <paramref name="other" /> in the sort order.
        /// </returns>
        public int CompareTo(MediaFrame other)
        {
            return StartTime.CompareTo(other.StartTime);
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (IsDisposed) return;

            if (alsoManaged)
            {
                Release();
                IsStale = true;
            }

            IsDisposed = true;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

    }

    /// <summary>
    /// Represents a wrapper for an unmanaged video frame.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.MediaFrame" />
    public unsafe sealed class VideoFrame : MediaFrame
    {
        #region Private Members

        private AVFrame* m_Pointer = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoFrame" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal VideoFrame(AVFrame* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            StartTime = frame->pts == ffmpeg.AV_NOPTS ?
                TimeSpan.FromTicks(component.Container.MediaStartTimeOffset.Ticks) :
                TimeSpan.FromTicks(frame->pts.ToTimeSpan(StreamTimeBase).Ticks - component.Container.MediaStartTimeOffset.Ticks);

            var repeatFactor = 1d + (0.5d * frame->repeat_pict);
            var timeBase = ffmpeg.av_guess_frame_rate(component.Container.InputContext, component.Stream, frame);
            Duration = repeatFactor.ToTimeSpan(new AVRational { num = timeBase.den, den = timeBase.num });
            EndTime = TimeSpan.FromTicks(StartTime.Ticks + Duration.Ticks);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public override MediaType MediaType => MediaType.Video;

        /// <summary>
        /// Gets the pointer to the unmanaged frame.
        /// </summary>
        internal AVFrame* Pointer { get { return m_Pointer; } }

        #endregion

        #region Methods

        /// <summary>
        /// Releases internal frame
        /// </summary>
        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);

            m_Pointer = null;
            InternalPointer = null;
        }

        #endregion

    }

    /// <summary>
    /// Represents a wrapper from an unmanaged audio frame
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.MediaFrame" />
    public unsafe sealed class AudioFrame : MediaFrame
    {
        #region Private Members

        private AVFrame* m_Pointer = null;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioFrame" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal AudioFrame(AVFrame* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // Compute the timespans
            //frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            StartTime = frame->pts == ffmpeg.AV_NOPTS ?
                TimeSpan.FromTicks(component.Container.MediaStartTimeOffset.Ticks) :
                TimeSpan.FromTicks(frame->pts.ToTimeSpan(StreamTimeBase).Ticks - component.Container.MediaStartTimeOffset.Ticks);

            // Compute the audio frame duration
            if (frame->pkt_duration != 0)
                Duration = frame->pkt_duration.ToTimeSpan(StreamTimeBase);
            else
                Duration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerMillisecond * 1000d * frame->nb_samples / frame->sample_rate, 0));

            EndTime = TimeSpan.FromTicks(StartTime.Ticks + Duration.Ticks);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public override MediaType MediaType => MediaType.Audio;

        /// <summary>
        /// Gets the pointer to the unmanaged frame.
        /// </summary>
        internal AVFrame* Pointer { get { return m_Pointer; } }

        #endregion

        #region Methods

        /// <summary>
        /// Releases internal frame
        /// </summary>
        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);

            m_Pointer = null;
            InternalPointer = null;
        }

        #endregion
    }

    /// <summary>
    /// Represents a wrapper for an unmanaged Subtitle frame.
    /// TODO: Only text subtitles are supported currently
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.MediaFrame" />
    public unsafe sealed class SubtitleFrame : MediaFrame
    {
        #region Private Members

        private AVSubtitle* m_Pointer = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleFrame" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal SubtitleFrame(AVSubtitle* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVSubtitle*)InternalPointer;

            // Extract timing information
            var timeOffset = TimeSpan.FromTicks(frame->pts.ToTimeSpan(StreamTimeBase).Ticks - component.Container.MediaStartTimeOffset.Ticks);
            StartTime = TimeSpan.FromTicks(timeOffset.Ticks + ((long)frame->start_display_time).ToTimeSpan(StreamTimeBase).Ticks);
            EndTime = TimeSpan.FromTicks(timeOffset.Ticks + ((long)frame->end_display_time).ToTimeSpan(StreamTimeBase).Ticks);
            Duration = TimeSpan.FromTicks(EndTime.Ticks - StartTime.Ticks);

            // Extract text strings
            for (var i = 0; i < frame->num_rects; i++)
            {
                var rect = frame->rects[i];
                if (rect->text != null)
                    Text.Add(Utils.PtrToStringUTF8(rect->text));
            }

            // Immediately release the frame as the struct was created in managed memory
            // Accessing it later will eventually caused a memory access error.
            Release();
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public override MediaType MediaType => MediaType.Subtitle;

        /// <summary>
        /// Gets the pointer to the unmanaged subtitle struct
        /// </summary>
        internal AVSubtitle* Pointer { get { return m_Pointer; } }

        /// <summary>
        /// Gets lines of text that the subtitle frame contains.
        /// </summary>
        public List<string> Text { get; } = new List<string>(16);

        #endregion

        #region Methods

        /// <summary>
        /// Releases internal frame
        /// </summary>
        protected override void Release()
        {
            if (m_Pointer == null) return;
            ffmpeg.avsubtitle_free(m_Pointer);
            m_Pointer = null;
            InternalPointer = null;
        }

        #endregion

    }
}
