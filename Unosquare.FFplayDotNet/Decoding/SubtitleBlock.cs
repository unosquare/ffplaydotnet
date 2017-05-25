namespace Unosquare.FFplayDotNet.Decoding
{
    using Core;
    using System.Collections.Generic;

    /// <summary>
    /// A subtitle frame container. Simply contains text lines.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.Core.MediaBlock" />
    public sealed class SubtitleBlock : MediaBlock
    {
        #region Properties

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public override MediaType MediaType => MediaType.Subtitle;

        /// <summary>
        /// Gets the lines of text for this subtitle frame.
        /// </summary>
        public List<string> Text { get; } = new List<string>(16);

        #endregion
    }
}
