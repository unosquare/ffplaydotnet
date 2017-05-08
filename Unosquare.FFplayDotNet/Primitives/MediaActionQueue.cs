namespace Unosquare.FFplayDotNet.Primitives
{
    using System.Collections.Generic;
    using System.Threading;

    public enum MediaAction
    {
        Default,
        ReadPackets,
        DecodeFrames,

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
        public MediaActionItem(MediaAction action, object arguments)
        {
            Arguments = arguments;
            Action = action;
        }

        public MediaAction Action { get; private set; }

        public object Arguments { get; private set; }

        public ManualResetEventSlim IsFinished { get; } = new ManualResetEventSlim(false);
    }

    public class MediaActionQueue
    {
        public static readonly MediaActionItem Default = new MediaActionItem(MediaAction.Default, null);

        private readonly object SyncRoot = new object();
        private readonly List<MediaActionItem> Queue = new List<MediaActionItem>();

        public MediaActionItem Push(MediaAction action, object arguments)
        {
            lock (SyncRoot)
            {
                var item = new MediaActionItem(action, arguments);
                Queue.Add(item);
                return item;
            }
                
        }

        public MediaActionItem Peek()
        {
            lock (SyncRoot)
            {
                if (Queue.Count > 0)
                    return Queue[0];
                else
                    return Default;

            }
        }

        public MediaActionItem Dequeue()
        {
            lock (SyncRoot)
            {
                if (Queue.Count <= 0)
                    return Default;

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
