namespace Unosquare.FFplayDotNet.Core
{

    public enum SyncMode
    {
        Audio,
        Video,
        External,
    }

    public enum MediaEventAction
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

}

