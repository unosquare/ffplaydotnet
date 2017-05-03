namespace Unosquare.FFplayDotNet.Console
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Unosquare.FFplayDotNet.Primitives;
    using Unosquare.Swan;

    class Program
    {
        static void Main(string[] args)
        {
            //  @"udp://@225.1.1.181:5181/"
            //  @"c:\users\unosp\Desktop\cowboys.mp4"
            var player = new MediaContainer(@"c:\users\unosp\Desktop\cowboys.mp4");
            var startTime = DateTime.Now;
            var packetsToDecode = 10000;

            for (var i = 0; i < packetsToDecode; i++)
            {
                player.Process();
                //if (player.IsMediaRealtime)
                //    Thread.Sleep(10);
            }

            $"Took {DateTime.Now.Subtract(startTime).TotalSeconds} seconds to decode {packetsToDecode} packets, {player.DecodedVideoFrames} fames, ({player.DecodedVideoFrames / player.Framerate}) seconds.".Info(typeof(Program));
            //player.OnVideoDataAvailable += Player_OnVideoDataAvailable;
            Terminal.ReadKey(true, true);
        }

        private static void Player_OnVideoDataAvailable(object sender, VideoDataEventArgs e)
        {
            $"Received Picture {e.BitmapData.Length/1024}KB, {e.BitmapWidth}x{e.BitmapHeight}".Info();
        }
    }
}
