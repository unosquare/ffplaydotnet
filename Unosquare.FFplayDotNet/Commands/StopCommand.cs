using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Commands
{
    /// <summary>
    /// Implements the logic to pause and rewind the media stream
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Commands.MediaCommand" />
    internal sealed class StopCommand : MediaCommand
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StopCommand"/> class.
        /// </summary>
        /// <param name="mediaElement">The media element.</param>
        public StopCommand(MediaElement mediaElement) 
            : base(mediaElement, MediaCommandType.Stop)
        {

        }

        protected override void Execute()
        {
            foreach (var renderer in MediaElement.Renderers.Values)
                renderer.Stop();

            MediaElement.Clock.Reset();
            MediaElement.Commands.Seek(TimeSpan.Zero);
            
        }
    }
}
