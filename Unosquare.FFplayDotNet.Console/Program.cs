namespace Unosquare.FFplayDotNet.Console
{
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
            var player = new FFplay(@"c:\users\unosp\Desktop\cowboys.mp4");
            player.OnVideoDataAvailable += Player_OnVideoDataAvailable;
            Terminal.ReadKey(true, true);
        }

        private static void Player_OnVideoDataAvailable(object sender, VideoDataEventArgs e)
        {
            $"Received Picture {e.BitmapData.Length/1024}KB, {e.BitmapWidth}x{e.BitmapHeight}".Info();
        }
    }
}
