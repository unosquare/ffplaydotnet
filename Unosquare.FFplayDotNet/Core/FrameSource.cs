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
    public unsafe abstract class FrameSource : IDisposable, IComparable<FrameSource>
    {

        #region Private Members

        protected void* InternalPointer;
        private bool IsDisposed = false;
        private TimeSpan? m_AbsoluteStartTime = null;
        private TimeSpan? m_AbsoluteEndTime = null;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameSource" /> class.
        /// </summary>
        /// <param name="pointer">The pointer.</param>
        /// <param name="component">The component.</param>
        internal FrameSource(void* pointer, MediaComponent component)
        {
            InternalPointer = pointer;
            StreamTimeBase = component.Stream->time_base;
            StreamStartTime = component.Stream->start_time.ToTimeSpan(StreamTimeBase);
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
        /// Gets the time at which this data should be presented (PTS)
        /// This timestamp has an offset of component stream's start time
        /// </summary>
        public TimeSpan RelativeStartTime { get; protected set; }


        /// <summary>
        /// Gets the end time (start pts time + duration)
        /// This timestamp has an offset of the compontnet stream's start time
        /// </summary>
        public TimeSpan RelativeEndTime { get; protected set; }


        /// <summary>
        /// Gets the absolute start time by removing the component stream's start time offset.
        /// This represents the zero-based, absolute start presentation timestamp.
        /// </summary>
        public TimeSpan StartTime
        {
            get
            {
                if (m_AbsoluteStartTime == null) m_AbsoluteStartTime = TimeSpan.FromTicks(RelativeStartTime.Ticks - StreamStartTime.Ticks);
                return m_AbsoluteStartTime.Value;
            }
        }

        /// <summary>
        /// Gets the absolute end time by removing the component stream's start time offset.
        /// This represents the zero-based, absolute end presentation timestamp.
        /// </summary>
        public TimeSpan EndTime
        {
            get
            {
                if (m_AbsoluteEndTime == null) m_AbsoluteEndTime = TimeSpan.FromTicks(RelativeEndTime.Ticks - StreamStartTime.Ticks);
                return m_AbsoluteEndTime.Value;
            }
        }

        /// <summary>
        /// Gets the time base of the stream that generated this frame.
        /// </summary>
        internal AVRational StreamTimeBase { get; }

        /// <summary>
        /// Gets the stream start time. Start Times and End times are relative to this
        /// Start Time.
        /// </summary>
        internal TimeSpan StreamStartTime { get; }

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
        public int CompareTo(FrameSource other)
        {
            return RelativeStartTime.CompareTo(other.RelativeStartTime);
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
    /// <seealso cref="Unosquare.FFplayDotNet.Core.FrameSource" />
    public unsafe sealed class VideoFrameSource : FrameSource
    {
        #region Private Members

        private AVFrame* m_Pointer = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoFrameSource" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal VideoFrameSource(AVFrame* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            RelativeStartTime = frame->pts.ToTimeSpan(StreamTimeBase);
            var repeatFactor = 1d + (0.5d * frame->repeat_pict);
            var timeBase = ffmpeg.av_guess_frame_rate(component.Container.InputContext, component.Stream, frame);
            Duration = repeatFactor.ToTimeSpan(new AVRational { num = timeBase.den, den = timeBase.num });
            RelativeEndTime =  TimeSpan.FromTicks(RelativeStartTime.Ticks + Duration.Ticks);
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
    /// <seealso cref="Unosquare.FFplayDotNet.Core.FrameSource" />
    public unsafe sealed class AudioFrameSource : FrameSource
    {
        #region Private Members

        private AVFrame* m_Pointer = null;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioFrameSource" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal AudioFrameSource(AVFrame* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // Compute the timespans
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            RelativeStartTime = frame->pts.ToTimeSpan(StreamTimeBase);

            // Compute the audio frame duration
            if (frame->pkt_duration != 0)
                Duration = frame->pkt_duration.ToTimeSpan(StreamTimeBase);
            else
                Duration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerMillisecond * 1000d * frame->nb_samples / frame->sample_rate, 0));

            RelativeEndTime = TimeSpan.FromTicks(RelativeStartTime.Ticks + Duration.Ticks);
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
    /// <seealso cref="Unosquare.FFplayDotNet.Core.FrameSource" />
    public unsafe sealed class SubtitleFrameSource : FrameSource
    {
        #region Private Members

        private AVSubtitle* m_Pointer = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleFrameSource" /> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <param name="component">The component.</param>
        internal SubtitleFrameSource(AVSubtitle* frame, MediaComponent component)
            : base(frame, component)
        {
            m_Pointer = (AVSubtitle*)InternalPointer;

            // Extract timing information
            var timeOffset = frame->pts.ToTimeSpan();
            RelativeStartTime = TimeSpan.FromTicks(timeOffset.Ticks + ((long)frame->start_display_time).ToTimeSpan(StreamTimeBase).Ticks);
            RelativeEndTime = TimeSpan.FromTicks(timeOffset.Ticks + ((long)frame->end_display_time).ToTimeSpan(StreamTimeBase).Ticks);
            Duration = TimeSpan.FromTicks(RelativeEndTime.Ticks - RelativeStartTime.Ticks);

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
