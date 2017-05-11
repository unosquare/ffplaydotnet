namespace Unosquare.FFplayDotNet.Core
{
    using FFmpeg.AutoGen;
    using System;

    public unsafe abstract class Frame : IDisposable
    {
        protected void* InternalPointer;
        protected AVRational TimeBase;
        private bool IsDisposed = false;

        internal Frame(MediaType mediaType, void* pointer, AVRational timeBase)
        {
            MediaType = mediaType;
            InternalPointer = pointer;
            TimeBase = timeBase;
        }

        public MediaType MediaType { get; private set; }

        /// <summary>
        /// Releases internal frame 
        /// </summary>
        protected abstract void Release();

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public TimeSpan StartTime { get; protected set; }

        /// <summary>
        /// Gets the end time (render time + duration)
        /// </summary>
        public TimeSpan EndTime { get; protected set; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; protected set; }

        #region IDisposable Support

        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged)
                    Release();

                IsDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

    }

    public unsafe class VideoFrame : Frame
    {
        private AVFrame* m_Pointer = null;

        public VideoFrame(AVFrame* frame, AVRational timeBase)
            : base(MediaType.Video, frame, timeBase)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
            StartTime = frame->pts.ToTimeSpan(timeBase);
            Duration = ffmpeg.av_frame_get_pkt_duration(frame).ToTimeSpan(timeBase);
            EndTime = StartTime + Duration;
        }

        public AVFrame* Pointer { get { return m_Pointer; } }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);

            m_Pointer = null;
        }
    }

    public unsafe class AudioFrame : Frame
    {
        private AVFrame* m_Pointer = null;

        public AudioFrame(AVFrame* frame, AVRational timeBase)
            : base(MediaType.Audio, frame, timeBase)
        {
            m_Pointer = (AVFrame*)InternalPointer;

            // Compute the timespans
            StartTime = ffmpeg.av_frame_get_best_effort_timestamp(frame).ToTimeSpan(timeBase);
            Duration = ffmpeg.av_frame_get_pkt_duration(frame).ToTimeSpan(timeBase);
            EndTime = StartTime + Duration;
        }

        public AVFrame* Pointer { get { return m_Pointer; } }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);

            m_Pointer = null;
        }
    }

    public unsafe class SubtitleFrame : Frame
    {
        private AVSubtitle* m_Pointer = null;

        public SubtitleFrame(AVSubtitle* frame, AVRational timeBase)
            : base(MediaType.Subtitle, frame, timeBase)
        {
            m_Pointer = (AVSubtitle*)InternalPointer;

            // Extract timing information
            var timeOffset = frame->pts.ToTimeSpan();
            StartTime = timeOffset + ((long)frame->start_display_time).ToTimeSpan(timeBase);
            EndTime = timeOffset + ((long)frame->end_display_time).ToTimeSpan(timeBase);
            Duration = EndTime - StartTime;
        }

        public AVSubtitle* Pointer { get { return m_Pointer; } }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            ffmpeg.avsubtitle_free(m_Pointer);
            m_Pointer = null;
        }
    }
}
