using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Unosquare.FFplayDotNet.Core;
using Unosquare.FFplayDotNet.Decoding;

namespace Unosquare.FFplayDotNet.Rendering
{
    internal sealed class VideoRenderer : IRenderer
    {
        private WriteableBitmap TargetBitmap;
        private MediaElement MediaElement;

        public VideoRenderer(MediaElement mediaElement)
        {
            MediaElement = mediaElement;
            Application.Current.Dispatcher.Invoke(() => {
                var visual = PresentationSource.FromVisual(MediaElement);
                var dpiX = 96.0 * visual?.CompositionTarget?.TransformToDevice.M11 ?? 96.0;
                var dpiY = 96.0 * visual?.CompositionTarget?.TransformToDevice.M22 ?? 96.0;

                if (MediaElement.HasVideo)
                    TargetBitmap = new WriteableBitmap(
                        MediaElement.NaturalVideoWidth, MediaElement.NaturalVideoHeight, dpiX, dpiY, PixelFormats.Bgr24, null);
                else
                    TargetBitmap = new WriteableBitmap(1, 1, dpiX, dpiY, PixelFormats.Bgr24, null);

                MediaElement.ViewBox.Source = TargetBitmap;
            });
        }

        public void Pause()
        {
            // placeholder
        }

        public void Play()
        {
            // placeholder
        }

        public void Render(MediaBlock mediaBlock, TimeSpan clockPosition, int renderIndex)
        {
            var block = mediaBlock as VideoBlock;
            if (block == null) return;

            Application.Current.Dispatcher.Invoke(() => {
                TargetBitmap.Lock();

                if (TargetBitmap.BackBufferStride != block.BufferStride)
                {
                    var sourceBase = block.Buffer;
                    var targetBase = TargetBitmap.BackBuffer;

                    for (var y = 0; y < TargetBitmap.PixelHeight; y++)
                    {
                        var sourceAddress = sourceBase + (block.BufferStride * y);
                        var targetAddress = targetBase + (TargetBitmap.BackBufferStride * y);
                        Utils.CopyMemory(targetAddress, sourceAddress, (uint)block.BufferStride);
                    }
                }
                else
                {
                    Utils.CopyMemory(TargetBitmap.BackBuffer, block.Buffer, (uint)block.BufferLength);
                }

                TargetBitmap.AddDirtyRect(new Int32Rect(0, 0, block.PixelWidth, block.PixelHeight));
                TargetBitmap.Unlock();
            });
        }

        public void Stop()
        {
            // placeholder
        }

        public void Close()
        {
            // TODO: Clear all pixels
        }
    }
}
