namespace Unosquare.FFplayDotNet.Console
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.Swan;

    class Program
    {
        #region Private Declarations

        private static string OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "output");

        private static MediaContainer Container;

        private static List<byte> AudioData = new List<byte>();
        private static double TotalDurationSeconds = 0d;
        private static ulong TotalBytes = 0;
        private static WriteableBitmap TargetBitmap = null;
        private static double Elapsed = 0d;
        private static double DecodeSpeed = 0d;

        private static Dictionary<MediaType, Frame> Outputs = new Dictionary<MediaType, Frame>
        {
            { MediaType.Video,  new VideoFrame() },
            { MediaType.Audio,  new AudioFrame() },
            { MediaType.Subtitle,  new SubtitleFrame() },

        };

        private static Dispatcher DecompressDispatcher = null;

        private static string InputFile = TestInputs.BigBuckBunnyLocal;
        private static double DecodeDurationLimit = 80;
        private static double StartTime = 0;
        private static bool IsBenchmarking = false;
        private static bool SaveWaveFile = false;
        private static bool SaveSnapshots = false;

        private static volatile bool ReadCancel = false;
        private static ManualResetEventSlim DecodingDone = new ManualResetEventSlim(false);
        private static ManualResetEventSlim DecodingFinished = new ManualResetEventSlim(false);

        #endregion

        #region Main Logic

        static void Main(string[] args)
        {

            InputFile = TestInputs.BigBuckBunnyLocal;
            StartTime = 10;
            DecodeDurationLimit = 10;
            IsBenchmarking = false;
            SaveWaveFile = false;
            SaveSnapshots = true;

            Container = new MediaContainer(InputFile);
            Container.MediaOptions.IsSubtitleDisabled = true;
            Container.Initialize();
            
            //TestNormalDecoding();
            var seekTimes = 0;
            var seekIncrement = Container.MediaDuration.TotalSeconds / 10d;

            for (var i = seekIncrement; i < Container.MediaDuration.TotalSeconds; i += seekIncrement)
            {
                TestSeeking(TimeSpan.FromSeconds(i));
                seekTimes++;
                //if (seekTimes >= 5) break;
            }

            Container.Dispose();

            Terminal.WriteLine("All Done!");
            Terminal.ReadKey(true, true);

        }

        private static void TestSeeking(TimeSpan target)
        {
            $"".Warn(typeof(Program));
            $"Target Time: {target.TotalSeconds,10:0.000}".Warn(typeof(Program));
            var decodedFrames = Container.StreamSeek(target, true);

            var videoFrame = decodedFrames.FirstOrDefault(f => f.MediaType == MediaType.Video);
            var audioFrame = decodedFrames.FirstOrDefault(f => f.MediaType == MediaType.Audio);

            $"Decoded Frames: {decodedFrames.Count} | Video: {videoFrame?.StartTime.TotalSeconds,10:0.0000} | Audio: {audioFrame?.StartTime.TotalSeconds,10:0.0000}".Debug(typeof(Program));

            var videoFrameCorrect = true;
            var audioFrameCorrect = true;
            if (videoFrame != null && videoFrame.StartTime > target)
                videoFrameCorrect = false;

            if (audioFrame != null && audioFrame.StartTime > target)
                audioFrameCorrect = false;

            if (videoFrameCorrect && audioFrameCorrect)
                $"Audio and Video Frames start before Start time. OK".Debug(typeof(Program));
            else
                $"Audio or Video Frames start after Start time. BAD SEEK".Error(typeof(Program));

            HandleDecoding(decodedFrames);
        }

        private static void TestNormalDecoding()
        {
            PrepareOutputDirectory(SaveWaveFile, SaveSnapshots);

            var chronometer = new Stopwatch();
            {
                chronometer.Start();
                var readerTask = RunReaderTask(); // Continuously read packets
                var decoderTask = RunDecoderTask(); // Continuously decode packets
                DecodingFinished.Wait();
                chronometer.Stop();
            }

            Elapsed = chronometer.ElapsedMilliseconds / 1000d;
            DecodeSpeed = Container.Components.Main.DecodedFrameCount / Elapsed;
            PrintResults();
        }

        private static void HandleDecoding(List<FrameSource> decodedFrames)
        {
            foreach (var frame in decodedFrames)
            {
                var frameResult = Outputs[frame.MediaType];
                Container.MaterializeFrame(frame, ref frameResult, true);
                if (IsBenchmarking == false)
                    HandleFrame(frameResult);
            }
        }

        private static ConfiguredTaskAwaitable RunReaderTask()
        {
            if (StartTime != 0)
            {
                var decodedFrames = Container.StreamSeek(TimeSpan.FromSeconds(StartTime), true);
                HandleDecoding(decodedFrames);
            }

            return Task.Run(() =>
            {
                while (ReadCancel == false)
                {
                    try
                    {
                        if (Container.IsAtEndOfStream == false && ReadCancel == false)
                        {
                            // check if the packet buuffer is too low
                            if (Container.Components.PacketBufferCount <= 24)
                            {
                                // buffer at least 60 packets
                                while (Container.Components.PacketBufferCount < 48 && Container.IsAtEndOfStream == false && ReadCancel == false)
                                    Container.ReadNext();

                                ($"Buffer     | DUR: {Container.Components.PacketBufferDuration.TotalSeconds,10:0.000}"
                                    + $" | LEN: {Container.Components.PacketBufferLength / 1024d,9:0.00}K"
                                    + $" | CNT: {Container.Components.PacketBufferCount,12}" + $" | POS: {Container.StreamPosition / 2014d,10:0.00}K")
                                    .Warn(typeof(Program));
                            }
                        }

                    }
                    finally
                    {
                        DecodingDone.Wait();
                    }
                }

                $"Reader task finished".Warn(typeof(Program));

            }).ConfigureAwait(false);
        }

        private static ConfiguredTaskAwaitable RunDecoderTask()
        {
            return Task.Run(() =>
            {
                if (DecompressDispatcher == null)
                    DecompressDispatcher = Dispatcher.CurrentDispatcher;

                var decodedSeconds = 0d;

                while (true)
                {
                    DecodingDone.Reset();

                    try
                    {
                        var decodedFrames = Container.DecodeNext(sortFrames: true);
                        HandleDecoding(decodedFrames);

                        if (decodedFrames.Count == 0)
                        {
                            // Alll decoding is done. Time to read
                            DecodingDone.Set();

                            // no more frames can be decoded now. Let's wait for more packets to arrive.
                            if (Container.IsStreamRealtime)
                                Thread.Sleep(1);
                        }
                        else
                        {
                            decodedSeconds += decodedFrames
                                .Where(f => f.MediaType == Container.Components.Main.MediaType)
                                .Sum(f => f.Duration.TotalSeconds);

                            if (Container.IsAtEndOfStream)
                            {
                                "End of file reached.".Warn(typeof(Program));
                                break;
                            }
                            else if (decodedSeconds >= DecodeDurationLimit)
                            {
                                ($"Decoder limit duration reached at {decodedFrames.Last().StartTime.TotalSeconds,8:0.00000} secs. " +
                                $"Limit was: {DecodeDurationLimit,8:0.00000} seconds").Info(typeof(Program));
                                break;
                            }
                        }


                    }
                    finally
                    {
                        DecodingDone.Set();
                    }
                }

                ReadCancel = true;
                DecodingDone.Set();
                $"Decoder task finished".Warn(typeof(Program));
                DecodingFinished.Set();

            }).ConfigureAwait(false);
        }

        #endregion

        #region Frame Handlers

        private static void HandleFrame(Frame e)
        {
            switch (e.MediaType)
            {
                case MediaType.Video:
                    HandleVideoFrame(e as VideoFrame);
                    return;
                case MediaType.Audio:
                    HandleAudioFrame(e as AudioFrame);
                    return;
                case MediaType.Subtitle:
                    HandleSubtitleFrame(e as SubtitleFrame);
                    return;
            }


        }

        private static void HandleAudioFrame(AudioFrame e)
        {
            TotalBytes += (ulong)e.BufferLength;
            //$"{e.MediaType,-10} | PTS: {e.StartTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {e.BufferLength / (float)1024,10:0.00}KB".Info(typeof(Program));

            if (IsBenchmarking) return;
            if (DecompressDispatcher == null) return;

            if (SaveWaveFile)
            {
                DecompressDispatcher.Invoke(() =>
                {
                    var outputBytes = new byte[e.BufferLength];
                    Marshal.Copy(e.Buffer, outputBytes, 0, outputBytes.Length);
                    AudioData.AddRange(outputBytes);
                });
            }
        }

        private static void HandleSubtitleFrame(SubtitleFrame e)
        {
            ("Subtitle: " + string.Join(" ", e.Text)).Warn(typeof(Program));
        }

        private static void HandleVideoFrame(VideoFrame e)
        {
            TotalBytes += (ulong)e.BufferLength;
            TotalDurationSeconds += e.Duration.TotalSeconds;
            $"{e.MediaType,-10} | PTS: {e.StartTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {e.BufferLength / (float)1024,10:0.00}KB".Info(typeof(Program));

            if (IsBenchmarking) return;
            if (DecompressDispatcher == null) return;

            DecompressDispatcher.Invoke(() =>
            {
                if (TargetBitmap == null)
                    TargetBitmap = new WriteableBitmap(e.PixelWidth, e.PixelHeight, 96, 96, PixelFormats.Bgr24, null);

                TargetBitmap.Dispatcher.Invoke(() =>
                {
                    TargetBitmap.Lock();

                    if (TargetBitmap.BackBufferStride != e.BufferStride)
                    {
                        var sourceBase = e.Buffer;
                        var targetBase = TargetBitmap.BackBuffer;

                        for (var y = 0; y < TargetBitmap.PixelHeight; y++)
                        {
                            var sourceAddress = sourceBase + (e.BufferStride * y);
                            var targetAddress = targetBase + (TargetBitmap.BackBufferStride * y);
                            Utils.CopyMemory(targetAddress, sourceAddress, (uint)e.BufferStride);
                        }
                    }
                    else
                    {
                        Utils.CopyMemory(TargetBitmap.BackBuffer, e.Buffer, (uint)e.BufferLength);
                    }

                    TargetBitmap.AddDirtyRect(new Int32Rect(0, 0, e.PixelWidth, e.PixelHeight));
                    TargetBitmap.Unlock();

                    if (SaveSnapshots == false) return;

                    var fileSequence = Math.Round(e.StartTime.TotalSeconds, 0);
                    var outputFile = Path.Combine(OutputPath, $"{fileSequence:0000}.png");

                    if (File.Exists(outputFile)) return;

                    var bitmapFrame = BitmapFrame.Create(TargetBitmap);
                    using (var stream = File.OpenWrite(outputFile))
                    {
                        var bitmapEncoder = new PngBitmapEncoder();
                        bitmapEncoder.Frames.Clear();
                        bitmapEncoder.Frames.Add(bitmapFrame);
                        bitmapEncoder.Save(stream);
                    }
                });
            });
        }

        #endregion

        #region Utility Methods

        private static void SaveWavFile(List<byte> audioData, string audioFile)
        {
            if (File.Exists(audioFile))
                File.Delete(audioFile);

            using (var file = File.OpenWrite(audioFile))
            {
                var bytesPerSample = 2;
                var spec = Outputs[MediaType.Audio] as AudioFrame;
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

        static private void PrintResults()
        {
            ($"Media Info\r\n" +
                $"    URL         : {Container.MediaUrl}\r\n" +
                $"    Bitrate     : {Container.MediaBitrate,10} bps\r\n" +
                $"    FPS         : {Container.Components.Video?.CurrentFrameRate,10:0.000}\r\n" +
                $"    Rel Start   : {Container.MediaRelativeStartTime.TotalSeconds,10:0.000}\r\n" +
                $"    Duration    : {Container.MediaDuration.TotalSeconds,10:0.000} secs\r\n" +
                $"    Seekable    : {Container.IsStreamSeekable,10}\r\n" +
                $"    Is Realtime : {Container.IsStreamRealtime,10}\r\n" +
                $"    Packets     : {Container.Components.ReceivedPacketCount,10}\r\n" +
                $"    Raw Data    : {TotalBytes / (double)(1024 * 1024),10:0.00} MB\r\n" +
                $"    Decoded     : {TotalDurationSeconds,10:0.000} secs\r\n" +
                $"    Decode FPS  : {DecodeSpeed,10:0.000}\r\n" +
                $"    Frames      : {Container.Components.Main.DecodedFrameCount,10}\r\n" +
                $"    Speed Ratio : {DecodeSpeed / Container.Components.Video?.CurrentFrameRate,10:0.000}\r\n" +
                $"    Benchmark T : {Elapsed,10:0.000} secs"
                ).Info(typeof(Program));


            if (SaveWaveFile && !IsBenchmarking)
            {
                var audioFile = Path.Combine(OutputPath, "audio.wav");
                SaveWavFile(AudioData, audioFile);
                $"Saved wave file to '{audioFile}'".Warn(typeof(Program));
            }
        }

        #endregion

    }
}
