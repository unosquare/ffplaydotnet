using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unosquare.FFplayDotNet.Primitives;

namespace Unosquare.FFplayDotNet
{

    public class VideoDataEventArgs : EventArgs
    {
        internal VideoDataEventArgs(FrameHolder frame)
        {
            var bitmapCopy = new byte[frame.Bitmap.Data.Length];
            Buffer.BlockCopy(frame.Bitmap.Data, 0, bitmapCopy, 0, bitmapCopy.Length);
            BitmapWidth = frame.PictureWidth;
            BitmapHeight = frame.PictureHeight;
            BitmapStride = frame.Bitmap.LineStride;

        }

        public byte[] BitmapData { get; }
        public int BitmapWidth { get; }
        public int BitmapHeight { get; }
        public int BitmapStride { get; }
    }

    public class SubtitleDataEventArgs : EventArgs
    {
        internal SubtitleDataEventArgs(FrameHolder frame)
        {
            // TODO: Insert subtitle info here...
            //frame->Subtitle.end_display_time
            //frame->Subtitle.rects[0]->type == FFmpeg.AutoGen.AVSubtitleType.
        }

    }
}
