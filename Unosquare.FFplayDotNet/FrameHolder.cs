namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using static Unosquare.FFplayDotNet.SDL;

    public unsafe class FrameHolder
    {
        public AVFrame* DecodedFrame;
        public AVSubtitle Subtitle;
        public int Serial;
        public double Pts;           /* presentation timestamp for the frame */
        public double EstimatedDuration;      /* estimated duration of the frame */
        public long BytePosition;          /* byte position of the frame in the input file */
        public SDL_Texture bmp;
        public bool IsAllocated;
        public int PictureWidth;
        public int PictureHeight;
        public int format;
        public AVRational PictureAspectRatio;
        public bool IsUploaded;
    }
}
