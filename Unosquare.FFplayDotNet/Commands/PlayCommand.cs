namespace Unosquare.FFplayDotNet.Commands
{
    /// <summary>
    /// Implements the logic to start or resume media playback
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Commands.MediaCommand" />
    internal sealed class PlayCommand : MediaCommand
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlayCommand"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public PlayCommand(MediaElement mediaElement) 
            : base(mediaElement, MediaCommandType.Play)
        {
        }

        /// <summary>
        /// Performs the actions that this command implements.
        /// </summary>
        protected override void Execute()
        {
            if (MediaElement.IsOpened == false) return;

            foreach (var renderer in MediaElement.Renderers.Values)
                renderer.Play();

            MediaElement.Clock.Play();
            MediaElement.MediaState = System.Windows.Controls.MediaState.Play;
        }
    }
}
