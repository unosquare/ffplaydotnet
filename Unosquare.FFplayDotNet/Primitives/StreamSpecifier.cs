namespace Unosquare.FFplayDotNet.Primitives
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    public class StreamSpecifier
    {

        public StreamSpecifier()
        {
            StreamType = string.Empty;
            StreamId = -1;
        }

        public StreamSpecifier(int streamId)
        {
            if (streamId < 0)
                throw new ArgumentException($"{nameof(streamId)} must be greater than or equal to 0");

            StreamType = string.Empty;
            StreamId = streamId;
        }

        public StreamSpecifier(char streamType)
        {
            if (streamType != 'a' && streamType != 'v' && streamType != 's')
                throw new ArgumentException($"{nameof(streamType)} must be either a, v, or s");

            StreamType = new string(streamType, 1);
            StreamId = -1;
        }

        public StreamSpecifier(char streamType, int streamId)
        {
            if (streamType != 'a' && streamType != 'v' && streamType != 's')
                throw new ArgumentException($"{nameof(streamType)} must be either a, v, or s");

            if (streamId < 0)
                throw new ArgumentException($"{nameof(streamId)} must be greater than or equal to 0");

            StreamType = new string(streamType, 1);
            StreamId = streamId;
        }

        public string StreamType { get; private set; }
        public int StreamId { get; private set; }

        public override string ToString()
        {
            if (string.IsNullOrWhiteSpace(StreamType) == false && StreamId >= 0)
                return $"{StreamType}:{StreamId}";

            if (string.IsNullOrWhiteSpace(StreamType) == false)
                return StreamType;

            if (StreamId >= 0)
                return StreamId.ToString(CultureInfo.InvariantCulture);

            return string.Empty;
        }
    }
}
