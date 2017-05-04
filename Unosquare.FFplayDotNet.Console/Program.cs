namespace Unosquare.FFplayDotNet.Console
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Unosquare.FFplayDotNet.Primitives;
    using Unosquare.Swan;

    class TestStreams
    {
        public const string BasePath = @"c:\users\unosp\Desktop\";

        public static string Mp4H264Regular = $"{BasePath}cowboys.mp4";

        public static string H264MulticastStream = @"udp://@225.1.1.181:5181/";

        public static string HlsMultiStream = @"http://qthttp.apple.com.edgesuite.net/1010qwoeiuryfg/sl.m3u8";

        /// <summary>
        /// Downloaded From: https://www.dropbox.com/sh/vggf640iniwxwyu/AABSeLJfAZeApEoJAY3N34Y2a?dl=0
        /// </summary>
        public static string MpegPart2 = $"{BasePath}big_buck_bunny_MPEG4.mp4";
        
        /// <summary>
        /// The mpg file form issue https://github.com/unosquare/ffmediaelement/issues/22
        /// </summary>
        public static string Mpg2 = $"{BasePath}22817BT_GTCTsang.mpg";

        /// <summary>
        /// The transport stream file
        /// From: https://github.com/unosquare/ffmediaelement/issues/16#issuecomment-299183167
        /// </summary>
        public static string TransportStreamFile = $"{BasePath}2013-12-18 22_45 - Anne Will.cut.ts";
    }

    class Program
    {
        
        static void Main(string[] args)
        {
            var player = new MediaContainer(TestStreams.Mpg2);
            player.OnVideoDataAvailable += (s, e) => {
                $"Video data avaialable at {e.Pts}. Picture buffer length is {e.BufferLength}".Info(typeof(Program));
            };

            var startTime = DateTime.Now;
            var packetsToDecode = 10000;
            var packetsDecoded = 0;
            for (var i = 0; i < packetsToDecode; i++)
            {
                player.Process();
                if (player.IsAtEndOfFile)
                {
                    "End of file reached".Info(typeof(Program));
                    break;
                }

                packetsDecoded += 1;
                //if (player.IsMediaRealtime)
                //    Thread.Sleep(10);
            }

            $"Took {DateTime.Now.Subtract(startTime).TotalSeconds} seconds to decode {packetsDecoded} packets, {player.Components.Video?.DecodedFrameCount} frames, {player.Components.Video?.Duration} seconds.".Info(typeof(Program));
            //player.OnVideoDataAvailable += Player_OnVideoDataAvailable;
            Terminal.ReadKey(true, true);
        }

        private static void Player_OnVideoDataAvailable(object sender, VideoDataEventArgs e)
        {
            $"Received Picture {e.BitmapData.Length/1024}KB, {e.BitmapWidth}x{e.BitmapHeight}".Info();
        }
    }
}
