namespace Unosquare.FFplayDotNet.Console
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.Swan;

    static class TestInputs
    {
        private static string InputBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "videos");

        #region Local Files

        /// <summary>
        /// The matroska test. It contains various subtitle an audio tracks
        /// Files can be obtained here: https://sourceforge.net/projects/matroska/files/test_files/matroska_test_w1_1.zip/download
        /// </summary>
        public static string MatroskaLocalFile = $"{InputBasePath}\\matroska.mkv";

        /// <summary>
        /// The transport stream file
        /// From: https://github.com/unosquare/ffmediaelement/issues/16#issuecomment-299183167
        /// </summary>
        public static string TransportLocalFile = $"{InputBasePath}\\transport.ts";

        /// <summary>
        /// Downloaded From: https://www.dropbox.com/sh/vggf640iniwxwyu/AABSeLJfAZeApEoJAY3N34Y2a?dl=0
        /// </summary>
        public static string BigBuckBunnyLocal = $"{InputBasePath}\\bigbuckbunny.mp4";

        public static string YoutubeLocalFile = $"{InputBasePath}\\youtube.mp4";

        /// <summary>
        /// The mpg file form issue https://github.com/unosquare/ffmediaelement/issues/22
        /// </summary>
        public static string MpegPart2LocalFile = $"{InputBasePath}\\mpegpart2.mpg";

        public static string PngTestLocalFile = $"{InputBasePath}\\pngtest.png";

        #endregion

        #region Network Files

        public static string UdpStream = @"udp://@225.1.1.181:5181/";

        public static string UdpStream2 = @"udp://@225.1.1.3:5003/";

        public static string HlsStream = @"http://qthttp.apple.com.edgesuite.net/1010qwoeiuryfg/sl.m3u8";

        /// <summary>
        /// The RTSP stream from http://g33ktricks.blogspot.mx/p/the-rtsp-real-time-streaming-protocol.html
        /// </summary>
        public static string RtspStream = "rtsp://184.72.239.149/vod/mp4:BigBuckBunny_175k.mov";

        public static string NetworkShareStream = @"\\STARBIRD\Dropbox\MEXICO 20120415 TOLUCA 0-3 CRUZ AZUL.mp4";

        public static string NetworkShareStream2 = @"\\STARBIRD\Public\Movies\Ender's Game (2013).mp4";

        #endregion
    }

    class Program
    {
        private static string OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "output");


        static void Main(string[] args)
        {

            // BigBuckBunnyLocal, 20 seconds, 1.036 secs.
            // TODO: 
            // May 5: 1.255 secs to decode 20 seconds 
            // May 8: 1.466 secs to decode 20 seconds
            // May 9: 0.688 secs to decode 20 seconds
            var inputFile = TestInputs.RtspStream;
            var decodeDurationLimit = 20d;
            var isBenchmarking = false;
            var saveWaveFile = true;
            var saveSnapshots = true;

            #region Setup

            var player = new MediaContainer(inputFile);
            PrepareOutputDirectory(saveWaveFile, saveSnapshots);
            var audioData = new List<byte>();
            WriteableBitmap bitmap = null;
            var totalDurationSeconds = 0d;
            ulong totalBytes = 0;

            #endregion

            #region Frame Handlers

            player.OnVideoDataAvailable += (s, e) =>
            {
                totalBytes += (ulong)e.BufferLength;
                totalDurationSeconds += e.Duration.TotalSeconds;
                $"{e.MediaType,-10} | PTS: {e.RenderTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {e.BufferLength / (float)1024,10:0.00}KB | LRT: {player.Components.Video.LastFrameRenderTime.TotalSeconds,10:0.000}".Info(typeof(Program));

                if (isBenchmarking) return;

                if (bitmap == null) bitmap = new WriteableBitmap(e.PixelWidth, e.PixelHeight, 96, 96, PixelFormats.Bgr24, null);

                bitmap.Lock();

                if (bitmap.BackBufferStride != e.BufferStride)
                {
                    var sourceBase = e.Buffer;
                    var targetBase = bitmap.BackBuffer;

                    for (var y = 0; y < bitmap.PixelHeight; y++)
                    {
                        var sourceAddress = sourceBase + (e.BufferStride * y);
                        var targetAddress = targetBase + (bitmap.BackBufferStride * y);
                        Utils.CopyMemory(targetAddress, sourceAddress, (uint)e.BufferStride);
                    }
                }
                else
                {
                    Utils.CopyMemory(bitmap.BackBuffer, e.Buffer, (uint)e.BufferLength);
                }

                bitmap.AddDirtyRect(new Int32Rect(0, 0, e.PixelWidth, e.PixelHeight));
                bitmap.Unlock();

                if (saveSnapshots == false) return;

                var fileSequence = Math.Round(e.RenderTime.TotalSeconds, 0);
                var outputFile = Path.Combine(OutputPath, $"{fileSequence:0000}.png");

                if (File.Exists(outputFile)) return;

                var bitmapFrame = BitmapFrame.Create(bitmap);
                using (var stream = File.OpenWrite(outputFile))
                {
                    var bitmapEncoder = new PngBitmapEncoder();
                    bitmapEncoder.Frames.Clear();
                    bitmapEncoder.Frames.Add(bitmapFrame);
                    bitmapEncoder.Save(stream);
                }

            };

            player.OnAudioDataAvailable += (s, e) =>
            {
                $"{e.MediaType,-10} | PTS: {e.RenderTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {e.BufferLength / (float)1024,10:0.00}KB | LRT: {player.Components.Video.LastFrameRenderTime.TotalSeconds,10:0.000}".Info(typeof(Program));

                totalBytes += (ulong)e.BufferLength;

                if (isBenchmarking) return;
                if (saveWaveFile)
                {
                    var outputBytes = new byte[e.BufferLength];
                    Marshal.Copy(e.Buffer, outputBytes, 0, outputBytes.Length);
                    audioData.AddRange(outputBytes);
                }

                
            };

            player.OnSubtitleDataAvailable += (s, e) =>
            {
                $"{e.MediaType,-10} | PTS: {e.RenderTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {"N/A",10:0}   | LRT: {player.Components.Video.LastFrameRenderTime.TotalSeconds,10:0.000}".Info(typeof(Program));
            };

            #endregion

            var chronometer = new Stopwatch();
            chronometer.Start();
            {
                var readCancel = false;
                var decodingDone = new ManualResetEventSlim(false);
                var decodingFinished = new ManualResetEventSlim(false);

                // Continuously read packets
                var readerTask = Task.Run(() =>
                {
                    while (readCancel == false)
                    {
                        try
                        {
                            if (player.IsAtEndOfStream == false && readCancel == false)
                            {
                                // check if the packet buuffer is too low
                                if (player.Components.PacketBufferCount <= 24)
                                {
                                    // buffer at least 60 packets
                                    while (player.Components.PacketBufferCount < 48)
                                        player.StreamReadNextPacket();

                                    ($"Buffer     | DUR: {player.Components.PacketBufferDuration.TotalSeconds,10:0.000}"
                                        + $" | LEN: {player.Components.PacketBufferLength / 1024d,9:0.00}K"
                                        + $" | CNT: {player.Components.PacketBufferCount,12}").Warn(typeof(Program));
                                }

                            }

                        }
                        finally
                        {
                            decodingDone.Wait();
                        }
                    }

                    $"Reader task finished".Warn(typeof(Program));

                }).ConfigureAwait(false);

                // Continuously decode packets
                var decoderTask = Task.Run(() =>
                {
                    while (true)
                    {
                        decodingDone.Reset();

                        try
                        {
                            player.Components.DecodeNextPacket();

                            var currentPosition =
                                player.Components.Video.LastFrameRenderTime.TotalSeconds
                                 - player.Components.Video.StartTime.TotalSeconds;

                            if (player.IsAtEndOfStream)
                            {
                                "End of file reached.".Warn(typeof(Program));
                                break;
                            }
                            else if (currentPosition >= decodeDurationLimit)
                            {
                                ($"Decoder limit duration reached at {currentPosition,8:0.00000} secs. " +
                                $"Limit was: {decodeDurationLimit,8:0.00000} seconds").Info(typeof(Program));
                                break;
                            }
                        }
                        finally
                        {
                            decodingDone.Set();
                        }
                    }

                    readCancel = true;
                    decodingDone.Set();
                    $"Decoder task finished".Warn(typeof(Program));
                    decodingFinished.Set();
                    
                }).ConfigureAwait(false);

                decodingFinished.Wait();
            }
            chronometer.Stop();

            var elapsed = chronometer.ElapsedMilliseconds / 1000d;
            var decodeSpeed = player.Components.Video.DecodedFrameCount / elapsed;

            ($"Media Info\r\n" +
                $"    URL         : {player.MediaUrl}\r\n" +
                $"    Bitrate     : {player.MediaBitrate,10} bps\r\n" +
                $"    FPS         : {player.Components.Video.CurrentFrameRate,10:0.000}\r\n" +
                $"    Start Time  : {player.MediaStartTime.TotalSeconds,10:0.000}\r\n" +
                $"    Duration    : {player.MediaDuration.TotalSeconds,10:0.000} secs\r\n" +
                $"    Seekable    : {player.IsStreamSeekable,10}\r\n" +
                $"    Can Suspend : {player.CanReadSuspend,10}\r\n" +
                $"    Is Realtime : {player.IsRealtimeStream,10}\r\n" +
                $"    Packets     : {player.Components.ReceivedPacketCount,10}\r\n" +
                $"    Raw Data    : {totalBytes / (double)(1024 * 1024),10:0.00} MB\r\n" +
                $"    Decoded     : {totalDurationSeconds,10:0.000} secs\r\n" +
                $"    Decode FPS  : {decodeSpeed,10:0.000}\r\n" +
                $"    Frames      : {player.Components.Video.DecodedFrameCount,10}\r\n" +
                $"    Speed Ratio : {decodeSpeed / player.Components.Video.CurrentFrameRate,10:0.000}\r\n" +
                $"    Benchmark T : {elapsed,10:0.000} secs"
                ).Info(typeof(Program));

            if (saveWaveFile && !isBenchmarking)
            {
                var audioFile = Path.Combine(OutputPath, "audio.wav");
                SaveWavFile(audioData, audioFile);
                $"Saved wave file to '{audioFile}'".Warn(typeof(Program));
            }

            Terminal.ReadKey(true, true);
        }

        private static void SaveWavFile(List<byte> audioData, string audioFile)
        {
            if (File.Exists(audioFile))
                File.Delete(audioFile);

            using (var file = File.OpenWrite(audioFile))
            {
                var bytesPerSample = 2;
                var spec = AudioParams.Output;
                using (var writer = new BinaryWriter(file))
                {
                    writer.Write("RIFF".ToCharArray()); // Group Id
                    writer.Write(0); // File Length (will be written later)
                    writer.Write("WAVE".ToCharArray()); // sRiffType
                    writer.Write("fmt ".ToCharArray()); // format chunk
                    writer.Write((uint)16); // the size of the header we just wrote (16 bytes)
                    writer.Write((ushort)1); // FormatTag (1 = MS PCM)
                    writer.Write((ushort)spec.ChannelCount); // channels
                    writer.Write((uint)spec.SampleRate); // sample rate
                    writer.Write((uint)(spec.SampleRate * spec.ChannelCount * bytesPerSample)); // nAvgBytesPerSec for buffer estimation samples * bytes per sample * channels
                    writer.Write((ushort)(bytesPerSample * spec.ChannelCount)); // nBlockAlign: block size is 2 bytes per sample times 2 channels
                    writer.Write((ushort)(bytesPerSample * 8)); // wBitsPerSample
                    writer.Write("data".ToCharArray()); // 
                    writer.Write((uint)audioData.Count); // this chunk size in bytes
                    writer.Write(audioData.ToArray());

                    // Set the total file length which is the byte count of the file minus the first 8 bytes
                    writer.Seek(4, SeekOrigin.Begin);
                    writer.Write((uint)(writer.BaseStream.Length - 8));
                }
            }
        }

        static private void PrepareOutputDirectory(bool cleanWavs, bool cleanPngs)
        {
            if (Directory.Exists(OutputPath) == false)
                Directory.CreateDirectory(OutputPath);

            var extensions = new List<string>();
            if (cleanWavs) extensions.Add("*.wav");
            if (cleanPngs) extensions.Add("*.png");

            foreach (var extension in extensions)
            {
                var entries = Directory.GetFiles(OutputPath, extension);
                foreach (var entry in entries)
                    File.Delete(entry);
            }
        }


    }
}
