using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unosquare.FFplayDotNet.Console
{
    class Program
    {
        static void Main(string[] args)
        {
            var player = new FFplayDotNet.FFplay();
            var microsecondsSince1970 = FFmpeg.AutoGen.ffmpeg.av_gettime();
            var dt = new DateTime(1970, 1, 1);
            dt = dt.AddMilliseconds(microsecondsSince1970 / 1000);



        }
    }
}
