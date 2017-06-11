namespace Unosquare.FFplayDotNet.Commands
{
    /// <summary>
    /// Implements the logic to pause the media stream
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Commands.MediaCommand" />
    internal sealed class PauseCommand : MediaCommand
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PauseCommand"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public PauseCommand(MediaElement mediaElement)
            : base(mediaElement, MediaCommandType.Pause)
        {
        }

        /// <summary>
        /// Performs the actions that this command implements.
        /// </summary>
        protected override void Execute()
        {
            if (MediaElement.IsOpened == false) return;
            if (MediaElement.CanPause == false) return;

            MediaElement.Clock.Pause();

            foreach (var renderer in MediaElement.Renderers.Values)
                renderer.Pause();

            if (MediaElement.MediaState != System.Windows.Controls.MediaState.Stop)
                MediaElement.MediaState = System.Windows.Controls.MediaState.Pause;
        }
    }
}
