namespace Unosquare.FFplayDotNet
{
    partial class FFplay
    {
        #region Enumerations

        public enum SyncMode
        {
            AV_SYNC_AUDIO_MASTER,
            AV_SYNC_VIDEO_MASTER,
            AV_SYNC_EXTERNAL_CLOCK,
        }

        public enum EventAction
        {
            AllocatePicture,
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

        #endregion
    }
}
