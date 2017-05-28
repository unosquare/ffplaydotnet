namespace Unosquare.FFplayDotNet.Console
{
    using Swan;
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Windows;

    class Program
    {



        static void Main(string[] args)
        {
            var controller = new PlaybackController();

            controller.MediaOpening += (s, e) => { $"EVT: {nameof(controller.MediaOpening)}".Info(typeof(Program)); };
            controller.MediaOpened += (s, e) => { $"EVT: {nameof(controller.MediaOpened)}".Info(typeof(Program)); };
            controller.BufferingStarted += (s, e) => { $"EVT: {nameof(controller.BufferingStarted)}".Info(typeof(Program)); };
            controller.BufferingEnded += (s, e) => { $"EVT: {nameof(controller.BufferingEnded)}".Info(typeof(Program)); };
            controller.MediaBlockAvailable += (s, e) => { $"EVT: {nameof(controller.MediaBlockAvailable)} | {e.Block.MediaType,8} | {e.Position.TotalSeconds,6:0.00}".Trace(typeof(Program)); };
            controller.MediaEnded += (s, e) => { $"EVT: {nameof(controller.MediaEnded)} | POS: {controller.Position.TotalSeconds,8:0.00}".Info(typeof(Program)); };

            var hasSeeked = false;
            controller.PropertyChanged += (s, e) =>
            {
                if (hasSeeked == false && controller.Position.TotalSeconds >= 3)
                {
                    hasSeeked = true;
                    controller.Position = TimeSpan.FromSeconds(40);
                    controller.SpeedRatio = 3.0d;
                }
            };

            controller.Open(TestInputs.MatroskaLocalFile);
            controller.Play();
            Terminal.ReadKey(true, true);
        }


    }
}
