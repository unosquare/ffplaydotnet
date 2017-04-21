namespace Unosquare.FFplayDotNet
{
    using System;
    using System.Threading;

    // TODO: revew creation and disposal, and review Pairs of calls

    public sealed class LockCondition : IDisposable
    {
        private AutoResetEvent ConditionDone = new AutoResetEvent(true);

        public LockCondition()
        {
            // placeholder
        }

        public void Wait(MonitorLock mutex)
        {
            Wait(mutex, -1);
        }

        public void Wait(MonitorLock mutex, int millisecondsTimeout)
        {
            var wasLocked = mutex.Unlock();

            if (millisecondsTimeout <= 0)
                ConditionDone.WaitOne();
            else
                ConditionDone.WaitOne(millisecondsTimeout);

            if (wasLocked) mutex.Lock();
        }

        public void Signal()
        {
            ConditionDone.Set();
        }

        #region IDisposable Support

        private bool IsDisposing = false; // To detect redundant calls

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                if (alsoManaged)
                {
                    ConditionDone.Dispose();
                }

                IsDisposing = true;
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
