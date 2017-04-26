namespace Unosquare.FFplayDotNet.Primitives
{
    using System.Collections.Generic;

    public enum MediaAction
    {
        None,
        Quit,
        ToggleFullScreen,
        TogglePause,
        ToggleMute,
        VolumeUp,
        VolumeDown,
        StepNextFrame,
        CycleAudio,
        CycleVideo,
        CycleSubtitles,
        CycleAll,
        NextChapter,
        PreviousChapter,
        SeekLeft10,
        SeekRight10,
        SeekLeft60,
        SeekLRight60,
    }

    public class MediaActionItem
    {
        public MediaActionItem(object sender, MediaAction action)
        {
            Sender = sender;
            Action = action;
        }

        public object Sender { get; private set; }
        public MediaAction Action { get; private set; }
    }

    public class MediaActionQueue
    {
        public static readonly MediaActionItem NoAction = new MediaActionItem(null, MediaAction.None);

        private readonly object SyncRoot = new object();
        private readonly List<MediaActionItem> Queue = new List<MediaActionItem>();

        public void Push(FFplay sender, MediaAction action)
        {
            lock (SyncRoot)
                Queue.Add(new MediaActionItem(sender, action));
        }

        public MediaActionItem Peek()
        {
            lock (SyncRoot)
            {
                if (Queue.Count > 0)
                    return Queue[0];
                else
                    return NoAction;

            }
        }

        public MediaActionItem Dequeue()
        {
            lock (SyncRoot)
            {
                if (Queue.Count <= 0)
                    return NoAction;

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
