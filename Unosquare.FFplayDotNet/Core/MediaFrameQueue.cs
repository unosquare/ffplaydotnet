namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Collections.Generic;

    public class MediaFrameQueue
    {
        #region Private Declarations

        private bool IsDisposed = false; // To detect redundant calls
        private readonly List<MediaFrame> Frames = new List<MediaFrame>();
        private readonly object SyncRoot = new object();
        private TimeSpan m_Duration = TimeSpan.Zero;

        #endregion

        #region Properties


        /// <summary>
        /// Gets or sets the <see cref="MediaFrame"/> at the specified index.
        /// </summary>
        /// <value>
        /// The <see cref="MediaFrame"/>.
        /// </value>
        /// <param name="index">The index.</param>
        /// <returns></returns>
        internal MediaFrame this[int index]
        {
            get
            {
                lock (SyncRoot)
                    return Frames[index];
            }
            set
            {
                lock (SyncRoot)
                    Frames[index] = value;
            }
        }

        /// <summary>
        /// Gets the frame count.
        /// </summary>
        public int Count
        {
            get
            {
                lock (SyncRoot)
                    return Frames.Count;
            }
        }

        /// <summary>
        /// Gets the total duration of all the frames contained in this queue.
        /// </summary>
        public TimeSpan Duration
        {
            get { lock (SyncRoot) return m_Duration; }
            private set { lock (SyncRoot) m_Duration = value; }
        }


        public TimeSpan StartTime { get { lock (SyncRoot) return Frames.Count == 0 ? TimeSpan.Zero : Frames[0].StartTime; } }

        public TimeSpan EndTime
        {
            get
            {
                lock (SyncRoot)
                {
                    if (Frames.Count == 0) return TimeSpan.Zero;
                    var lastFrame = Frames[Frames.Count - 1];
                    return TimeSpan.FromTicks(lastFrame.StartTime.Ticks + lastFrame.Duration.Ticks);
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Peeks the next available frame in the queue without removing it.
        /// If no frames are available, null is returned.
        /// </summary>
        /// <returns></returns>
        public MediaFrame Peek()
        {
            lock (SyncRoot)
            {
                if (Frames.Count <= 0) return null;
                return Frames[0];
            }
        }

        /// <summary>
        /// Pushes the specified frame into the queue.
        /// In other words, enqueues the frame.
        /// </summary>
        /// <param name="frame">The frame.</param>
        public void Push(MediaFrame frame)
        {
            lock (SyncRoot)
            {
                Frames.Add(frame);
                Duration = TimeSpan.FromTicks(Duration.Ticks + frame.Duration.Ticks);
            }

        }

        /// <summary>
        /// Dequeues a frame from this queue.
        /// </summary>
        /// <returns></returns>
        public MediaFrame Dequeue()
        {
            lock (SyncRoot)
            {
                if (Frames.Count <= 0) return null;
                var frame = Frames[0];
                Frames.RemoveAt(0);

                Duration = TimeSpan.FromTicks(Duration.Ticks - frame.Duration.Ticks);
                return frame;
            }
        }

        /// <summary>
        /// Clears and frees all frames from this queue.
        /// </summary>
        public void Clear()
        {
            lock (SyncRoot)
            {
                while (Frames.Count > 0)
                {
                    var frame = Dequeue();
                    frame.Dispose();
                    frame = null;
                }

                Duration = TimeSpan.Zero;
            }
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                if (alsoManaged)
                    Clear();
            }
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
}
