namespace Unosquare.FFplayDotNet.Primitives
{
    using System.Collections.Generic;
    using Unosquare.FFplayDotNet.Core;
    using System.Linq;

    public class MediaEvent
    {
        public MediaEvent(object sender, MediaEventAction action)
        {
            Sender = sender;
            Action = action;
        }

        public object Sender { get; private set; }
        public MediaEventAction Action { get; private set; }
    }

    public class MediaEventQueue
    {
        private readonly object SyncRoot = new object();
        private readonly List<MediaEvent> Queue = new List<MediaEvent>();

        public void PushEvent(FFplay sender, MediaEventAction action)
        {
            lock (SyncRoot)
                Queue.Add(new MediaEvent(sender, action));
        }


        public MediaEvent Dequeue()
        {
            lock (SyncRoot)
            {
                if (Queue.Count <= 0) return new MediaEvent(this, MediaEventAction.None);
                var item = Queue[0];
                Queue.RemoveAt(0);
                return item;
            }
        }

        public int Count
        {
            get
            {
                lock (SyncRoot)
                    return Queue.Count;
            }
        }

        public void Clear()
        {
            lock (SyncRoot)
            {
                Queue.Clear();
            }

        }
    }
}
