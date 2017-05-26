namespace Unosquare.FFplayDotNet.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Decoding;

    public class MediaOpeningEventArgs : EventArgs
    {
        internal MediaOpeningEventArgs(MediaOptions options)
            : base()
        {
            Options = options;
        }

        public MediaOptions Options { get; private set; }
    }

    public class MediaFailedEventArgs : EventArgs
    {
        internal MediaFailedEventArgs(Exception exception)
            : base()
        {
            Exception = exception;
        }

        public Exception Exception { get; private set; }
    }

    public class MediaBlockAvailableEventArgs : EventArgs
    {
        internal MediaBlockAvailableEventArgs(MediaBlock block, TimeSpan position)
        {
            Block = block;
            Position = position;
        }

        public MediaBlock Block { get; private set; }
        public TimeSpan Position { get; private set; }
    }
}
