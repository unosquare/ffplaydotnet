namespace Unosquare.FFplayDotNet.Console
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
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

        private static MediaContainer Player;

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
        private static bool IsBenchmarking = true;
        private static bool SaveWaveFile = true;
        private static bool SaveSnapshots = true;

        private static volatile bool ReadCancel = false;
        private static ManualResetEventSlim DecodingDone = new ManualResetEventSlim(false);
        private static ManualResetEventSlim DecodingFinished = new ManualResetEventSlim(false);

        #endregion

        #region Main Logic

        static void Main(string[] args)
        {

            #region Setup

            InputFile = TestInputs.BigBuckBunnyLocal;
            DecodeDurationLimit = 20;
            IsBenchmarking = false;
            SaveWaveFile = false;
            SaveSnapshots = true;

            Player = new MediaContainer(InputFile);
            PrepareOutputDirectory(SaveWaveFile, SaveSnapshots);

            #endregion

            var chronometer = new Stopwatch();
            {
                chronometer.Start();
                var readerTask = RunReaderTask(); // Continuously read packets
                var decoderTask = RunDecoderTask(); // Continuously decode packets
                DecodingFinished.Wait();
                chronometer.Stop();
            }
            
            Elapsed = chronometer.ElapsedMilliseconds / 1000d;
            DecodeSpeed = Player.Components.Video.DecodedFrameCount / Elapsed;
            PrintResults();
            Terminal.ReadKey(true, true);
        }

        private static ConfiguredTaskAwaitable RunReaderTask()
        {
            return Task.Run(() =>
            {
                while (ReadCancel == false)
                {
                    try
                    {

                        if (Player.IsAtEndOfStream == false && ReadCancel == false)
                        {
                            // check if the packet buuffer is too low
                            if (Player.Components.PacketBufferCount <= 24)
                            {
                                // buffer at least 60 packets
                                while (Player.Components.PacketBufferCount < 48 && Player.IsAtEndOfStream == false && ReadCancel == false)
                                    Player.ReadNext();

                                ($"Buffer     | DUR: {Player.Components.PacketBufferDuration.TotalSeconds,10:0.000}"
                                    + $" | LEN: {Player.Components.PacketBufferLength / 1024d,9:0.00}K"
                                    + $" | CNT: {Player.Components.PacketBufferCount,12}" + $" | POS: {Player.StreamPosition / 2014d,10:0.00}K")
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

                while (true)
                {
                    DecodingDone.Reset();

                    try
                    {
                        var decodedFrames = Player.DecodeNext(sortFrames: true);
                        foreach (var frame in decodedFrames)
                        {
                            var frameResult = Outputs[frame.MediaType];
                            Player.MaterializeFrame(frame, ref frameResult, true);
                            if (IsBenchmarking == false)
                                HandleFrame(frameResult);
                        }

                        if (decodedFrames.Count == 0)
                        {
                            // Alll decoding is done. Time to read
                            DecodingDone.Set();

                            // no more frames can be decoded now. Let's wait for more packets to arrive.
                            if (Player.IsRealtimeStream)
                                Thread.Sleep(1);
                        }
                        else
                        {
                            var currentPosition =
                                Player.Components.Video.LastFrameTime.TotalSeconds
                                 - Player.Components.Video.StartTime.TotalSeconds;

                            if (Player.IsAtEndOfStream)
                            {
                                "End of file reached.".Warn(typeof(Program));
                                break;
                            }
                            else if (currentPosition >= DecodeDurationLimit)
                            {
                                ($"Decoder limit duration reached at {currentPosition,8:0.00000} secs. " +
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
            $"{e.MediaType,-10} | PTS: {e.StartTime.TotalSeconds,8:0.00000} | DUR: {e.Duration.TotalSeconds,8:0.00000} | BUF: {e.BufferLength / (float)1024,10:0.00}KB | LRT: {Player.Components.Video.LastFrameTime.TotalSeconds,10:0.000}".Info(typeof(Program));

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
                $"    URL         : {Player.MediaUrl}\r\n" +
                $"    Bitrate     : {Player.MediaBitrate,10} bps\r\n" +
                $"    FPS         : {Player.Components.Video.CurrentFrameRate,10:0.000}\r\n" +
                $"    Start Time  : {Player.MediaStartTime.TotalSeconds,10:0.000}\r\n" +
                $"    Duration    : {Player.MediaDuration.TotalSeconds,10:0.000} secs\r\n" +
                $"    Seekable    : {Player.IsStreamSeekable,10}\r\n" +
                $"    Is Realtime : {Player.IsRealtimeStream,10}\r\n" +
                $"    Packets     : {Player.Components.ReceivedPacketCount,10}\r\n" +
                $"    Raw Data    : {TotalBytes / (double)(1024 * 1024),10:0.00} MB\r\n" +
                $"    Decoded     : {TotalDurationSeconds,10:0.000} secs\r\n" +
                $"    Decode FPS  : {DecodeSpeed,10:0.000}\r\n" +
                $"    Frames      : {Player.Components.Video.DecodedFrameCount,10}\r\n" +
                $"    Speed Ratio : {DecodeSpeed / Player.Components.Video.CurrentFrameRate,10:0.000}\r\n" +
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
