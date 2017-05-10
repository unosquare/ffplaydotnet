namespace Unosquare.FFplayDotNet.Core
{
    using FFmpeg.AutoGen;
    using System;

    public unsafe abstract class Frame : IDisposable
    {
        protected void* ObjectPointer;
        private bool IsDisposed = false;
        protected AVRational TimeBase;

        protected Frame(MediaType mediaType, void* pointer, AVRational timeBase)
        {
            MediaType = MediaType;
            ObjectPointer = pointer;
            TimeBase = timeBase;
        }

        public MediaType MediaType { get; private set; }

        public void* Opaque { get { return ObjectPointer; } }

        protected abstract void Release();

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public abstract TimeSpan StartTime { get; protected set; }

        /// <summary>
        /// Gets the end time (render time + duration)
        /// </summary>
        public virtual TimeSpan EndTime { get { return StartTime + Duration; } }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public abstract TimeSpan Duration { get; protected set; }

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
            m_Pointer = (AVFrame*)ObjectPointer;
        }

        public AVFrame* Pointer { get { return m_Pointer; } }

        public override TimeSpan StartTime { get; protected set; }

        public override TimeSpan Duration { get; protected set; }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);
        }
    }

    public unsafe class AudioFrame : Frame
    {
        private AVFrame* m_Pointer = null;

        public AudioFrame(AVFrame* frame, AVRational timeBase)
            : base(MediaType.Audio, frame, timeBase)
        {
            m_Pointer = (AVFrame*)ObjectPointer;
        }

        public AVFrame* Pointer { get { return m_Pointer; } }

        public override TimeSpan StartTime { get; protected set; }

        public override TimeSpan Duration { get; protected set; }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            fixed (AVFrame** pointer = &m_Pointer)
                ffmpeg.av_frame_free(pointer);
        }
    }

    public unsafe class SubtitleFrame : Frame
    {
        private AVSubtitle* m_Pointer = null;

        public SubtitleFrame(AVSubtitle* frame, AVRational timeBase)
            : base(MediaType.Audio, frame, timeBase)
        {
            m_Pointer = (AVSubtitle*)ObjectPointer;
        }

        public AVSubtitle* Pointer { get { return m_Pointer; } }

        public override TimeSpan StartTime { get; protected set; }

        public override TimeSpan Duration { get; protected set; }

        protected override void Release()
        {
            if (m_Pointer == null) return;
            ffmpeg.avsubtitle_free(m_Pointer);
        }
    }
}
