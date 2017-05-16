namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// A time measurement artifact.
    /// </summary>
    public class Clock
    {
        private readonly Stopwatch Chrono = new Stopwatch();
        private double OffsetMilliseconds = 0;
        private readonly object SyncLock = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="Clock"/> class.
        /// The clock starts poaused and at the 0 position.
        /// </summary>
        public Clock()
        {
            Reset();
        }

        /// <summary>
        /// Gets or sets the clock position.
        /// </summary>
        public TimeSpan Position
        {
            get
            {
                lock (SyncLock)
                    return TimeSpan.FromMilliseconds(OffsetMilliseconds + Chrono.ElapsedMilliseconds);
            }
            set
            {
                lock (SyncLock)
                {
                    var resume = Chrono.IsRunning;
                    Chrono.Reset();
                    OffsetMilliseconds = value.TotalMilliseconds;
                    if (resume) Chrono.Start();
                }

            }
        }

        /// <summary>
        /// Gets a value indicating whether the clock is running.
        /// </summary>
        public bool IsRunning { get { lock (SyncLock) return Chrono.IsRunning; } }

        /// <summary>
        /// Starts or resumes the clock.
        /// </summary>
        public void Play()
        {
            lock (SyncLock)
            {
                if (Chrono.IsRunning) return;
                Chrono.Start();
            }

        }

        /// <summary>
        /// Pauses the clock.
        /// </summary>
        public void Pause()
        {
            Chrono.Stop();
        }

        /// <summary>
        /// Sets the clock position to 0 and stops it.
        /// </summary>
        public void Reset()
        {
            lock (SyncLock)
            {
                OffsetMilliseconds = 0;
                Chrono.Reset();
            }

        }
    }

}
