namespace Unosquare.FFplayDotNet.Primitives
{
    using FFmpeg.AutoGen;
    using static Unosquare.FFplayDotNet.SDL;

    /// <summary>
    /// A class that holds decoded frames of any media type.
    /// Port of struct Frame
    /// </summary>
    public unsafe class FrameHolder
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrameHolder"/> class.
        /// </summary>
        public FrameHolder()
        {
            // placeholder
        }

        /// <summary>
        /// The decoded frame. Port of *frame
        /// </summary>
        public AVFrame* DecodedFrame;

        /// <summary>
        /// Gets the type of the media of this frame.
        /// </summary>
        public AVMediaType MediaType { get; internal set; }

        /// <summary>
        /// The decoded subtitle (if it is a subtitle frame)
        /// </summary>
        public AVSubtitle Subtitle;

        /// <summary>
        /// The serial number of the last packet that
        /// made up the frame
        /// </summary>
        public int Serial { get; internal set; }
        
        /// <summary>
        /// The PTS: presentation timestamp for the frame
        /// </summary>
        public double Pts { get; internal set; }
        
        /// <summary>
        /// The estimated duration of the frame
        /// </summary>
        public double Duration { get; internal set; }

        /// <summary>
        /// The byte position of the frame in the input file
        /// </summary>
        public long BytePosition;

        public SDL_Texture bmp;

        public bool IsAllocated;
        public AVRational PictureAspectRatio;
        public int PictureWidth;
        public int PictureHeight;
        public int format;
        public bool IsUploaded;
    }
}
