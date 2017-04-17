namespace Unosquare.FFplayDotNet
{
    partial class FFplay
    {
        #region Enumerations

        internal enum SyncMode
        {
            AV_SYNC_AUDIO_MASTER,
            AV_SYNC_VIDEO_MASTER,
            AV_SYNC_EXTERNAL_CLOCK,
        }

        internal enum ShowMode
        {
            SHOW_MODE_NONE = -1, SHOW_MODE_VIDEO = 0 //, SHOW_MODE_WAVES, SHOW_MODE_RDFT, SHOW_MODE_NB
        }

        #endregion
    }
}
