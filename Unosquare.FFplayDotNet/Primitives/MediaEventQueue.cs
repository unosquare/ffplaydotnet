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
        private readonly Queue<MediaEvent> Queue = new Queue<MediaEvent>();

        public void PushEvent(FFplay sender, MediaEventAction action)
        {
            lock (SyncRoot)
                Queue.Enqueue(new MediaEvent(sender, action));
        }

        public int CountEvents(MediaEventAction action)
        {
            lock (SyncRoot)
            {
                return Queue.Count(c => c.Action == action);
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
