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

        /// <summary>
        /// Gets the events.
        /// Port of SDL_PeepEvents (with argument get event)
        /// </summary>
        /// <param name="action">The action.</param>
        /// <returns></returns>
        public MediaEvent[] DequeueEvents(MediaEventAction action)
        {
            lock (SyncRoot)
            {
                var result = new List<MediaEvent>();
                for(var i = Queue.Count - 1; i >= 0; i--)
                {
                    result.Add(Queue[i]);
                    Queue.RemoveAt(i);
                }

                result.Reverse();
                return result.ToArray();
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
