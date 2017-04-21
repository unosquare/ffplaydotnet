namespace Unosquare.FFplayDotNet
{
    using System.Threading;

    // TODO: revew creation and disposal, and review Pairs of calls

    public sealed class MonitorLock
    {
        private readonly object SyncRoot = new object();

        public MonitorLock()
        {

        }

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

        public void Destroy()
        {
            Unlock();
        }
    }

}
