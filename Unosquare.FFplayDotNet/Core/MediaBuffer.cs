using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Core
{

    /// <summary>
    /// Contains a set of pre-allocated Media Slots
    /// </summary>
    public class MediaFrameBuffer<T>
        where T : MediaFrameSlot
    {
        protected List<T> Slots;

        protected MediaFrameBuffer(int capacity)
        {
            Slots = new List<T>(capacity);

        }



    }

    public class VideoFrameBuffer : MediaFrameBuffer<VideoFrameSlot>
    {
        public VideoFrameBuffer(int capacity) : base(capacity)
        {
        }
    }

    public class MediaBufferSet
    {

        public MediaBufferSet()
        {
            
        }

        public VideoFrameBuffer Video { get; private set; }

    }
}
