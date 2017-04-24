namespace Unosquare.FFplayDotNet
{

    public class BitmapBuffer
    {
        /// <summary>
        /// Contains all the bytes of the pixel data
        /// Each horizontal scanline is represented by LineLength
        /// rather than by LinceStride. The left-over stride bytes
        /// are removed
        /// </summary>
        public byte[] Data { get; internal set; }

        /// <summary>
        /// Gets the width of the image.
        /// </summary>
        public int ImageWidth { get; internal set; }

        /// <summary>
        /// Gets the height of the image.
        /// </summary>
        public int ImageHeight { get; internal set; }

        /// <summary>
        /// Gets the length in bytes of a line of pixel data.
        /// Basically the same as Line Length except Stride might be a little larger as
        /// some bitmaps might be DWORD-algned
        /// </summary>
        public int LineStride { get; internal set; }

        /// <summary>
        /// Gets the length in bytes of a line of pixel data.
        /// Basically the same as Stride except Stride might be a little larger as
        /// some bitmaps might be DWORD-algned
        /// </summary>
        public int LineLength { get; internal set; }
    }

}
