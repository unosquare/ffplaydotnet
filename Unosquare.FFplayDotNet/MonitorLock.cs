namespace Unosquare.FFplayDotNet
{
    using System.Threading;

    // TODO: revew creation and disposal, and review Pairs of calls

    /// <summary>
    /// Represents a lightweight mutex (a monitor)
    /// This is a port of SDL_mutex
    /// </summary>
    public sealed class MonitorLock
    {
        private readonly object SyncRoot = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="MonitorLock"/> class.
        /// Port of SDL_CreateMutex
        /// </summary>
        public MonitorLock()
        {

        }

        /// <summary>
        /// Locks this instance.
        /// Port of SDL_LockMutex
        /// </summary>
        /// <returns></returns>
        public bool Lock()
        {
            var lockWasTaken = false;
            try
            {
                Monitor.Enter(SyncRoot, ref lockWasTaken);
            }
            catch
            {
                // placeholder
            }

            return lockWasTaken;
        }

        /// <summary>
        /// Unlocks this instance.
        /// Port of SDL_UnlockMutex
        /// </summary>
        /// <returns></returns>
        public bool Unlock()
        {
            try
            {
                if (Monitor.IsEntered(SyncRoot))
                {
                    Monitor.Exit(SyncRoot);
                    return true;
                }
            }
            catch
            {
                // placeholder
            }

            return false;
        }

        /// <summary>
        /// Destroys this instance. This really just unlocks the monitor
        /// Thei si a port of SDL_DestroyMutex
        /// </summary>
        public void Destroy()
        {
            Unlock();
        }
    }

}
