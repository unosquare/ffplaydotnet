namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.FFplayDotNet.Primitives;
    using Unosquare.Swan;

    /// <summary>
    /// Enumerates the different Media Types
    /// </summary>
    public enum MediaType
    {
        /// <summary>
        /// The video media type (0)
        /// </summary>
        Video = 0,
        /// <summary>
        /// The audio media type (1)
        /// </summary>
        Audio = 1,
        /// <summary>
        /// The subtitle media type (3)
        /// </summary>
        Subtitle = 3,
    }

    /// <summary>
    /// Provides a set of utilities to perfrom conversion and other
    /// miscellaneous calculations
    /// </summary>
    internal static class MediaUtils
    {
        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts, AVRational timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.MinValue;

            if (timeBase.den == 0)
                return TimeSpan.FromSeconds(pts / ffmpeg.AV_TIME_BASE);

            return TimeSpan.FromSeconds(pts * timeBase.num / timeBase.den);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts, AVRational timeBase)
        {
            return ((double)pts).ToTimeSpan(timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts, double timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.MinValue;

            return TimeSpan.FromSeconds(pts / timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp and a timebase.
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <param name="timeBase">The time base.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts, double timeBase)
        {
            return ((double)pts).ToTimeSpan(timeBase);
        }

        /// <summary>
        /// Gets a timespan given a timestamp (in AV_TIME_BASE units)
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this double pts)
        {
            return ToTimeSpan(pts, ffmpeg.AV_TIME_BASE);
        }

        /// <summary>
        /// Gets a timespan given a timestamp (in AV_TIME_BASE units)
        /// </summary>
        /// <param name="pts">The PTS.</param>
        /// <returns></returns>
        public static TimeSpan ToTimeSpan(this long pts)
        {
            return ((double)pts).ToTimeSpan();
        }
    }

    /// <summary>
    /// A Media Container Exception
    /// </summary>
    /// <seealso cref="System.Exception" />
    public class MediaContainerException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaContainerException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public MediaContainerException(string message) : base(message) { }
    }

    /// <summary>
    /// Represents a base class for uncompressed media events
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    public abstract class MediaDataAvailableEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        protected MediaDataAvailableEventArgs(MediaType mediaType, TimeSpan renderTime, TimeSpan duration)
        {
            MediaType = mediaType;
            RenderTime = renderTime;
            Duration = duration;
        }

        /// <summary>
        /// Gets the time at which this data should be presented (PTS)
        /// </summary>
        public TimeSpan RenderTime { get; }

        /// <summary>
        /// Gets the amount of time this data has to be presented
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the media type of the data
        /// </summary>
        public MediaType MediaType { get; }
    }

    /// <summary>
    /// Contains result data for subtitle frame processing.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaDataAvailableEventArgs" />
    public class SubtitleDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="textLines">The text lines.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="endTime">The end time.</param>
        /// <param name="duration">The duration.</param>
        internal SubtitleDataAvailableEventArgs(string[] textLines, TimeSpan renderTime, TimeSpan endTime, TimeSpan duration)
            : base(MediaType.Subtitle, renderTime, duration)
        {
            TextLines = textLines ?? new string[] { };
            EndTime = endTime;
        }

        /// <summary>
        /// Gets the lines of text to be displayed.
        /// </summary>
        public string[] TextLines { get; }

        /// <summary>
        /// Gets the time at which this subtitle must stop displaying
        /// </summary>
        public TimeSpan EndTime { get; }

    }

    /// <summary>
    /// Contains result data for audio frame processing.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaDataAvailableEventArgs" />
    public class AudioDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="sampleRate">The sample rate.</param>
        /// <param name="samplesPerChannel">The samples per channel.</param>
        /// <param name="channels">The channels.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal AudioDataAvailableEventArgs(IntPtr buffer, int bufferLength, int sampleRate, int samplesPerChannel, int channels,
            TimeSpan renderTime, TimeSpan duration)
            : base(MediaType.Audio, renderTime, duration)
        {
            Buffer = buffer;
            BufferLength = bufferLength;
            SamplesPerChannel = samplesPerChannel;
            SampleRate = sampleRate;
            Channels = channels;
        }

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format is interleaved stereo 16-bit, signed samples (4 bytes per sample in stereo)
        /// </summary>
        public IntPtr Buffer { get; }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get; }

        /// <summary>
        /// Gets the sample rate of the buffer.
        /// Typically this is 4.8kHz
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Gets the number of samples in the data per individual audio channel.
        /// </summary>
        public int SamplesPerChannel { get; }

        /// <summary>
        /// Gets the number of channels the data buffer represents.
        /// </summary>
        public int Channels { get; }
    }

    /// <summary>
    /// Contains result data for video frame processing.
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    public class VideoDataAvailableEventArgs : MediaDataAvailableEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VideoDataAvailableEventArgs"/> class.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="bufferStride">The buffer stride.</param>
        /// <param name="width">The width.</param>
        /// <param name="height">The height.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal VideoDataAvailableEventArgs(IntPtr buffer, int bufferLength, int bufferStride, int width, int height,
            TimeSpan renderTime, TimeSpan duration)
            : base(MediaType.Video, renderTime, duration)
        {
            Buffer = buffer;
            BufferLength = bufferLength;
            BufferStride = bufferStride;
            PixelWidth = width;
            PixelHeight = height;
        }

        /// <summary>
        /// Gets a pointer to the first byte of the data buffer.
        /// The format is 24bit in BGR
        /// </summary>
        public IntPtr Buffer { get; }

        /// <summary>
        /// Gets the length of the buffer in bytes.
        /// </summary>
        public int BufferLength { get; }

        /// <summary>
        /// Gets the number of bytes per scanline in the image
        /// </summary>
        public int BufferStride { get; }

        /// <summary>
        /// Gets the number of horizontal pixels in the image.
        /// </summary>
        public int PixelWidth { get; }

        /// <summary>
        /// Gets the number of vertical pixels in the image.
        /// </summary>
        public int PixelHeight { get; }
    }

    /// <summary>
    /// A data structure containing a quque of packets to process.
    /// This class is thread safe and disposable.
    /// Enqueued, unmanaged packets are disposed automatically by this queue.
    /// Dequeued packets are the responsibility of the calling code.
    /// </summary>
    internal unsafe class MediaPacketQueue : IDisposable
    {
        #region Private Declarations

        private bool IsDisposing = false; // To detect redundant calls
        private readonly List<IntPtr> PacketPointers = new List<IntPtr>();
        private readonly object SyncRoot = new object();

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the <see cref="AVPacket"/> at the specified index.
        /// </summary>
        /// <value>
        /// The <see cref="AVPacket"/>.
        /// </value>
        /// <param name="index">The index.</param>
        /// <returns></returns>
        public AVPacket* this[int index]
        {
            get
            {
                lock (SyncRoot)
                    return (AVPacket*)PacketPointers[index];
            }
            set
            {
                lock (SyncRoot)
                    PacketPointers[index] = (IntPtr)value;
            }
        }

        /// <summary>
        /// Gets the packet count.
        /// </summary>
        public int Count
        {
            get
            {
                lock (SyncRoot)
                    return PacketPointers.Count;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Peeks the next available packet in the queue without removing it.
        /// If no packets are available, null is returned.
        /// </summary>
        /// <returns></returns>
        public AVPacket* Peek()
        {
            lock (SyncRoot)
            {
                if (PacketPointers.Count <= 0) return null;
                return (AVPacket*)PacketPointers[0];
            }
        }

        /// <summary>
        /// Pushes the specified packet into the queue.
        /// In other words, enqueues the packet.
        /// </summary>
        /// <param name="packet">The packet.</param>
        public void Push(AVPacket* packet)
        {
            lock (SyncRoot)
                PacketPointers.Add((IntPtr)packet);
        }

        /// <summary>
        /// Dequeues a packet from this queue.
        /// </summary>
        /// <returns></returns>
        public AVPacket* Dequeue()
        {
            lock (SyncRoot)
            {
                if (PacketPointers.Count <= 0) return null;
                var result = PacketPointers[0];
                PacketPointers.RemoveAt(0);
                return (AVPacket*)result;
            }
        }

        /// <summary>
        /// Clears and frees all the unmanaged packets from this queue.
        /// </summary>
        public void Clear()
        {
            lock (SyncRoot)
            {
                while (PacketPointers.Count > 0)
                {
                    var packet = Dequeue();
                    ffmpeg.av_packet_free(&packet);
                }
            }
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                IsDisposing = true;
                if (alsoManaged)
                    Clear();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }

    /// <summary>
    /// Contains audio format properties essential
    /// to audio resampling
    /// </summary>
    public sealed unsafe class AudioComponentSpec
    {
        #region Constant Definitions

        /// <summary>
        /// The standard output audio spec
        /// </summary>
        static public readonly AudioComponentSpec Output;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes the <see cref="AudioComponentSpec"/> class.
        /// </summary>
        static AudioComponentSpec()
        {
            Output = new AudioComponentSpec();
            Output.ChannelCount = 2;
            Output.SampleRate = 48000;
            Output.Format = AVSampleFormat.AV_SAMPLE_FMT_S16;
            Output.ChannelLayout = ffmpeg.av_get_default_channel_layout(Output.ChannelCount);
            Output.SamplesPerChannel = Output.SampleRate + 256;
            Output.BufferLength = ffmpeg.av_samples_get_buffer_size(
                null, Output.ChannelCount, Output.SamplesPerChannel, Output.Format, 1);
        }

        /// <summary>
        /// Prevents a default instance of the <see cref="AudioComponentSpec"/> class from being created.
        /// </summary>
        private AudioComponentSpec() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioComponentSpec"/> class.
        /// </summary>
        /// <param name="frame">The frame.</param>
        private AudioComponentSpec(AVFrame* frame)
        {
            ChannelCount = ffmpeg.av_frame_get_channels(frame);
            ChannelLayout = ffmpeg.av_frame_get_channel_layout(frame);
            Format = (AVSampleFormat)frame->format;
            SamplesPerChannel = frame->nb_samples;
            BufferLength = ffmpeg.av_samples_get_buffer_size(null, ChannelCount, SamplesPerChannel, Format, 1);
            SampleRate = frame->sample_rate;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the channel count.
        /// </summary>
        public int ChannelCount { get; private set; }

        /// <summary>
        /// Gets the channel layout.
        /// </summary>
        public long ChannelLayout { get; private set; }

        /// <summary>
        /// Gets the samples per channel.
        /// </summary>
        public int SamplesPerChannel { get; private set; }

        /// <summary>
        /// Gets the audio sampling rate.
        /// </summary>
        public int SampleRate { get; private set; }

        /// <summary>
        /// Gets the sample format.
        /// </summary>
        public AVSampleFormat Format { get; private set; }

        /// <summary>
        /// Gets the length of the buffer required to store 
        /// the samples in the current format.
        /// </summary>
        public int BufferLength { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a source audio spec based on the info in the given audio frame
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <returns></returns>
        static internal AudioComponentSpec CreateSource(AVFrame* frame)
        {
            return new AudioComponentSpec(frame);
        }

        /// <summary>
        /// Creates a target audio spec using the sample quantities provided 
        /// by the given source audio frame
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <returns></returns>
        static internal AudioComponentSpec CreateTarget(AVFrame* frame)
        {
            var spec = new AudioComponentSpec
            {
                ChannelCount = Output.ChannelCount,
                Format = Output.Format,
                SampleRate = Output.SampleRate,
                ChannelLayout = Output.ChannelLayout
            };

            // The target transform is just a ratio of the source frame's sample. This is how many samples we desire
            spec.SamplesPerChannel = (int)Math.Round((double)frame->nb_samples * spec.SampleRate / frame->sample_rate, 0) + 256;
            spec.BufferLength = ffmpeg.av_samples_get_buffer_size(null, spec.ChannelCount, spec.SamplesPerChannel, spec.Format, 1);
            return spec;
        }

        /// <summary>
        /// Determines if the audio specs are compatible between them.
        /// They must share format, channel count, layout and sample rate
        /// </summary>
        /// <param name="a">a.</param>
        /// <param name="b">The b.</param>
        /// <returns></returns>
        static public bool AreCompatible(AudioComponentSpec a, AudioComponentSpec b)
        {
            if (a.Format != b.Format) return false;
            if (a.ChannelCount != b.ChannelCount) return false;
            if (a.ChannelLayout != b.ChannelLayout) return false;
            if (a.SampleRate != b.SampleRate) return false;

            return true;
        }

        #endregion

    }

    /// <summary>
    /// Represents a media component of a given media type within a 
    /// media container. Derived classes must implement frame handling
    /// logic.
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe abstract class MediaComponent : IDisposable
    {

        #region Constants

        /// <summary>
        /// Contains constants defining dictionary entry names for codec options
        /// </summary>
        protected static class CodecOption
        {
            public const string Threads = "threads";
            public const string RefCountedFrames = "refcounted_frames";
            public const string LowRes = "lowres";
        }

        #endregion

        #region Private Declarations

        /// <summary>
        /// Detects redundant, unmanaged calls to the Dispose method.
        /// </summary>
        private bool IsDisposing = false;

        /// <summary>
        /// Holds a reference to a flush packet. 
        /// It will be freed upon disposal of this object.
        /// </summary>
        private AVPacket* FlushPacket;

        /// <summary>
        /// Holds a reference to the Codec Context.
        /// </summary>
        internal AVCodecContext* CodecContext;

        /// <summary>
        /// Holds a reference to the associated input context stream
        /// </summary>
        internal AVStream* Stream;

        /// <summary>
        /// Contains the packets pending to be sent to the decoder
        /// </summary>
        internal readonly MediaPacketQueue Packets = new MediaPacketQueue();

        /// <summary>
        /// The packets that have been sent to the decoder. We keep track of them in order to dispose them
        /// once a frame has been decoded.
        /// </summary>
        internal readonly MediaPacketQueue SentPackets = new MediaPacketQueue();

        #endregion

        #region Properties

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public MediaType MediaType { get; }

        /// <summary>
        /// Gets the media container associated with this component.
        /// </summary>
        public MediaContainer Container { get; }

        /// <summary>
        /// Gets the index of the associated stream.
        /// </summary>
        public int StreamIndex { get; }

        /// <summary>
        /// Gets the number of frames that have been decoded by this component
        /// </summary>
        public int DecodedFrameCount { get; private set; }

        /// <summary>
        /// Gets the start time of this stream component.
        /// If there is no such information it will return TimeSpan.MinValue
        /// </summary>
        public TimeSpan StartTime { get; }

        /// <summary>
        /// Gets the duration of this stream component.
        /// If there is no such information it will return TimeSpan.MinValue
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the end time of this stream component.
        /// If there is no such information it will return TimeSpan.MinValue
        /// </summary>
        public TimeSpan EndTime { get; }

        /// <summary>
        /// Gets the time in UTC at which the last frame was processed.
        /// </summary>
        public DateTime LastProcessedTimeUTC { get; protected set; }

        /// <summary>
        /// Gets the render time if the last processed frame.
        /// </summary>
        public TimeSpan LastProcessedRenderTime { get; protected set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        /// <exception cref="System.ArgumentNullException">container</exception>
        /// <exception cref="System.Exception"></exception>
        protected MediaComponent(MediaContainer container, int streamIndex)
        {
            // NOTE: code largely based on stream_component_open
            Container = container ?? throw new ArgumentNullException(nameof(container));
            CodecContext = ffmpeg.avcodec_alloc_context3(null);
            StreamIndex = streamIndex;
            Stream = container.InputContext->streams[StreamIndex];

            // Set codec options
            var setCodecParamsResult = ffmpeg.avcodec_parameters_to_context(CodecContext, Stream->codecpar);

            if (setCodecParamsResult < 0)
                $"Could not set codec parameters. Error code: {setCodecParamsResult}".Warn(typeof(MediaContainer));

            // We set the packet timebase in the same timebase as the stream as opposed to the tpyical AV_TIME_BASE
            ffmpeg.av_codec_set_pkt_timebase(CodecContext, Stream->time_base);

            // Find the codec and set it.
            var codec = ffmpeg.avcodec_find_decoder(Stream->codec->codec_id);
            if (codec == null)
            {
                var errorMessage = $"Fatal error. Unable to find suitable decoder for {Stream->codec->codec_id.ToString()}";
                Dispose();
                throw new MediaContainerException(errorMessage);
            }

            CodecContext->codec_id = codec->id;

            // Process the low res index option
            var lowResIndex = ffmpeg.av_codec_get_max_lowres(codec);
            if (Container.Options.EnableLowRes)
            {
                ffmpeg.av_codec_set_lowres(CodecContext, lowResIndex);
                CodecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;
            }
            else
            {
                lowResIndex = 0;
            }

            // Configure the codec context flags
            if (Container.Options.EnableFastDecoding) CodecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0) CodecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;
            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_TRUNCATED) != 0) CodecContext->flags |= ffmpeg.AV_CODEC_CAP_TRUNCATED;
            if ((codec->capabilities & ffmpeg.CODEC_FLAG2_CHUNKS) != 0) CodecContext->flags |= ffmpeg.CODEC_FLAG2_CHUNKS;

            // Setup additional settings. The most important one is Threads -- Setting it to 1 decoding is very slow. Setting it to auto
            // decoding is very fast in most scenarios.
            var codecOptions = Container.Options.CodecOptions.FilterOptions(CodecContext->codec_id, Container.InputContext, Stream, codec);
            if (codecOptions.HasKey(CodecOption.Threads) == false) codecOptions[CodecOption.Threads] = "auto";
            if (lowResIndex != 0) codecOptions[CodecOption.LowRes] = lowResIndex.ToString(CultureInfo.InvariantCulture);
            if (CodecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || CodecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                codecOptions[CodecOption.RefCountedFrames] = 1.ToString(CultureInfo.InvariantCulture);

            // Open the CodecContext
            var codecOpenResult = ffmpeg.avcodec_open2(CodecContext, codec, codecOptions.Reference);
            if (codecOpenResult < 0)
            {
                Dispose();
                throw new MediaContainerException($"Unable to open codec. Error code {codecOpenResult}");
            }

            // If there are any codec options left over from passing them, it means they were not consumed
            if (codecOptions.First() != null)
                $"Codec Option '{codecOptions.First().Key}' not found.".Warn(typeof(MediaContainer));

            // Startup done. Set some options.
            Stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            MediaType = (MediaType)CodecContext->codec_type;

            // Compute the start time
            if (Stream->start_time == ffmpeg.AV_NOPTS_VALUE)
                StartTime = Container.InputContext->start_time.ToTimeSpan();
            else
                StartTime = Stream->start_time.ToTimeSpan(Stream->time_base);

            // compute the duration
            if (Stream->duration == ffmpeg.AV_NOPTS_VALUE || Stream->duration == 0)
                Duration = Container.InputContext->duration.ToTimeSpan();
            else
                Duration = Stream->duration.ToTimeSpan(Stream->time_base);

            // compute the end time
            if (StartTime != TimeSpan.MinValue && Duration != TimeSpan.MinValue)
                EndTime = StartTime + Duration;
            else
                EndTime = TimeSpan.MinValue;

            $"{MediaType}: Start Time: {StartTime}; End Time: {EndTime}; Duration: {Duration}".Trace(typeof(MediaContainer));

            // Create a temporary flush packet (will be released upon Dispose)
            // Flush packets are used to drain the old API decoder (subtitles in this case) when
            // there is more than 1 frame in a single packet
            FlushPacket = ffmpeg.av_packet_alloc();
            FlushPacket->data = null;
            FlushPacket->size = 0;

        }

        #endregion

        #region Methods

        /// <summary>
        /// Determines whether the specified packet is a Flush Packet (data = null, size = 0)
        /// </summary>
        protected bool IsFlushPacket(AVPacket* packet)
        {
            if (packet == null || packet == FlushPacket) return true;
            return (packet->data == null && packet->size == 0);
        }

        /// <summary>
        /// Clears the pending and sent Packet Queues releasing all memory held by those packets.
        /// Additionally it flushes the codec buffered packets.
        /// </summary>
        public void ClearPacketQueues()
        {
            // Discard any data that was buffered in codec's internal memory.
            // reset the buffer
            if (CodecContext != null)
                ffmpeg.avcodec_flush_buffers(CodecContext);

            // Release packets that are already in the queue.
            SentPackets.Clear();
            Packets.Clear();
        }

        /// <summary>
        /// Sends a special kind of packet (a flush packet)
        /// that tells the decoder to enter draining mode.
        /// </summary>
        public virtual void SendFlushPacket()
        {
            SendPacket(FlushPacket);
        }

        /// <summary>
        /// Pushes a packet into the decoding Packet Queue
        /// and processes the packet in order to try to decode
        /// 1 or more frames. The packet has to be within the range of
        /// the start time and end time of 
        /// </summary>
        /// <param name="packet">The packet.</param>
        public virtual void SendPacket(AVPacket* packet)
        {
            // TODO: check if packet is in play range
            // ffplay.c reference: pkt_in_play_range

            Packets.Push(packet);
            while (Packets.Count > 0)
                DecodedFrameCount += ReceiveFramesFromPacketQueue();
        }

        /// <summary>
        /// Receives 0 or more frames from the current Packet Queue.
        /// This sends the first available packet to dequeue to the decoder
        /// and uses the decoded frames (if any) to their corresponding
        /// ProcessFrame method.
        /// </summary>
        /// <returns></returns>
        protected virtual int ReceiveFramesFromPacketQueue()
        {
            // Ensure there is at least one packet in the queue
            if (Packets.Count <= 0) return 0;

            // Setup some initial state variables
            var packet = Packets.Peek();
            var receiveFrameResult = 0;
            var receivedFrameCount = 0;

            if (MediaType == MediaType.Audio || MediaType == MediaType.Video)
            {
                // If it's audio or video, we use the new API and the decoded frames are stored in AVFrame
                // Let us send the packet to the codec for decoding a frame of uncompressed data later
                var sendPacketResult = ffmpeg.avcodec_send_packet(CodecContext, IsFlushPacket(packet) ? null : packet);


                // Check if the send operation was successful. If not, the decoding buffer might be full
                // We will keep the packet in the queue to process it later.
                if (sendPacketResult != ffmpeg.AVERROR_EAGAIN)
                    SentPackets.Push(Packets.Dequeue());

                // Let's check and see if we can get 1 or more frames from the packet we just sent to the decoder.
                // Audio packets will typically contain 1 or more audioframes
                // Video packets might require several packets to decode 1 frame
                while (receiveFrameResult == 0)
                {
                    // Allocate a frame in unmanaged memory and 
                    // Try to receive the decompressed frame data
                    var outputFrame = ffmpeg.av_frame_alloc();
                    receiveFrameResult = ffmpeg.avcodec_receive_frame(CodecContext, outputFrame);

                    try
                    {
                        // Process the output frame if we were successful on a different thread if possible
                        // That is, using a new task
                        if (receiveFrameResult == 0)
                        {
                            // Send the frame to processing
                            receivedFrameCount += 1;
                            ProcessFrame(packet, outputFrame);
                        }
                    }
                    finally
                    {

                        // Release the frame as the decoded data has been processed 
                        // regardless if there was any output.
                        ffmpeg.av_frame_free(&outputFrame);
                    }
                }
            }
            else if (MediaType == MediaType.Subtitle)
            {
                // Fors subtitles we use the old API (new API send_packet/receive_frame) is not yet available
                var gotFrame = 0;
                var outputFrame = new AVSubtitle(); // We create the struct in managed memory as there is no API to create a subtitle.
                receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, &outputFrame, &gotFrame, packet);
                SentPackets.Push(Packets.Dequeue());

                // Check if there is an error decoding the packet.
                // If there is, remove the packet clear the sent packets
                if (receiveFrameResult < 0)
                {
                    ffmpeg.avsubtitle_free(&outputFrame);
                    SentPackets.Clear();
                    $"{MediaType}: Error decoding. Error Code: {receiveFrameResult}".Error(typeof(MediaContainer));
                }
                else
                {
                    // Process the first frame if we got it from the packet
                    // Note that there could be more frames (subtitles) in the packet
                    if (gotFrame != 0)
                    {
                        // Send the frame to processing
                        receivedFrameCount += 1;
                        ProcessFrame(packet, &outputFrame);
                    }

                    // Once processed, we don't need it anymore. Release it.
                    ffmpeg.avsubtitle_free(&outputFrame);

                    // Let's check if we have more decoded frames from the same single packet
                    // Packets may contain more than 1 frame and the decoder is drained
                    // by passing a flush packet (data = null, size = 0)
                    while (gotFrame != 0 || receiveFrameResult > 0)
                    {
                        outputFrame = new AVSubtitle();
                        receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, &outputFrame, &gotFrame, FlushPacket);
                        if (gotFrame != 0)
                        {
                            // Send the subtitle to processing
                            receivedFrameCount += 1;
                            ProcessFrame(packet, &outputFrame);
                        }

                        // once the subtitle is processed. Release it from memory
                        ffmpeg.avsubtitle_free(&outputFrame);
                    }
                }
            }

            // Release the sent packets if 1 or more frames were received in the packet
            if (receivedFrameCount >= 1)
            {
                // We clear the sent packet queue (releasing packet from unmanaged memory also)
                // because we got at least 1 frame from the packet.
                SentPackets.Clear();
            }

            return receivedFrameCount;
        }

        /// <summary>
        /// Processes the audio or video frame.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected abstract void ProcessFrame(AVPacket* packet, AVFrame* frame);

        /// <summary>
        /// Processes the subtitle frame.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected abstract void ProcessFrame(AVPacket* packet, AVSubtitle* frame);

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                if (alsoManaged)
                {
                    if (CodecContext != null)
                    {
                        fixed (AVCodecContext** codecContext = &CodecContext)
                            ffmpeg.avcodec_free_context(codecContext);

                        // release the Flush packet
                        fixed (AVPacket** flushPacket = &FlushPacket)
                            ffmpeg.av_packet_free(flushPacket);

                        // free all the pending and sent packets
                        ClearPacketQueues();
                    }

                    CodecContext = null;
                }

                IsDisposing = true;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

    }

    /// <summary>
    /// Performs video picture decoding, scaling and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class VideoComponent : MediaComponent
    {
        #region Private State Variables

        /// <summary>
        /// Holds a reference to the video scaler
        /// </summary>
        private SwsContext* Scaler = null;

        /// <summary>
        /// Holds a reference to the last allocated buffer
        /// </summary>
        private IntPtr PictureBuffer;

        /// <summary>
        /// The picture buffer length of the last allocated buffer
        /// </summary>
        private int PictureBufferLength;

        /// <summary>
        /// The picture buffer stride. 
        /// Pixel Width * 24-bit color (3 byes) + alignment (typically 0 for modern hw).
        /// </summary>
        private int PictureBufferStride;



        #endregion

        #region Constants

        /// <summary>
        /// Gets the video scaler flags used to perfom colorspace conversion (if needed).
        /// </summary>
        public static int ScalerFlags { get; internal set; } = ffmpeg.SWS_X; //ffmpeg.SWS_BICUBIC;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        internal VideoComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            BaseFrameRate = ffmpeg.av_q2d(Stream->r_frame_rate);
            CurrentFrameRate = BaseFrameRate;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the base frame rate as reported by the stream component.
        /// All discrete timestamps can be represented in this framerate.
        /// </summary>
        public double BaseFrameRate { get; }

        /// <summary>
        /// Gets the current frame rate as guessed by the last processed frame.
        /// Variable framerate might report different values at different times.
        /// </summary>
        public double CurrentFrameRate { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Processes the frame data by performing a framebuffer allocation, scaling the image
        /// and raising an event containing the bitmap.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastProcessedRenderTime = frame->pts.ToTimeSpan(Stream->time_base);

            // Update the current framerate
            CurrentFrameRate = ffmpeg.av_q2d(ffmpeg.av_guess_frame_rate(Container.InputContext, Stream, frame));

            // If we don't have a callback, we don't need any further processing
            if (Container.HandlesOnVideoDataAvailable == false)
                return;

            // Retrieve a suitable scaler or create it on the fly
            Scaler = ffmpeg.sws_getCachedContext(Scaler,
                    frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height,
                    Constants.OutputPixelFormat, ScalerFlags, null, null, null);

            // Perform scaling and save the data to our unmanaged buffer pointer for callbacks
            {
                PictureBufferStride = ffmpeg.av_image_get_linesize(Constants.OutputPixelFormat, frame->width, 0);
                var targetStride = new int[] { PictureBufferStride };
                var targetLength = ffmpeg.av_image_get_buffer_size(Constants.OutputPixelFormat, frame->width, frame->height, 1);
                var unmanagedBuffer = AllocateBuffer(targetLength);
                var targetScan = new byte_ptrArray8();
                targetScan[0] = (byte*)unmanagedBuffer;
                var outputHeight = ffmpeg.sws_scale(Scaler, frame->data, frame->linesize, 0, frame->height, targetScan, targetStride);
            }

            // Raise the data available event with all the decompressed frame data
            var duration = ffmpeg.av_frame_get_pkt_duration(frame);
            Container.RaiseOnVideoDataAvailabe(
                PictureBuffer, PictureBufferLength, PictureBufferStride,
                frame->width, frame->height,
                frame->pts.ToTimeSpan(Stream->time_base),
                duration.ToTimeSpan(Stream->time_base));

        }

        /// <summary>
        /// Processes the subtitle frame.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        /// <exception cref="System.NotSupportedException"></exception>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            throw new NotSupportedException($"{nameof(VideoComponent)} does not support subtitle frame processing.");
        }

        /// <summary>
        /// Allocates a buffer if needed in unmanaged memory. If we already have a buffer of the specified
        /// length, then the existing buffer is not freed and recreated. Regardless, this method will always return
        /// a pointer to the start of the buffer.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private IntPtr AllocateBuffer(int length)
        {
            // If there is a size mismatch between the wanted buffer length and the existing one,
            // then let's reallocate the buffer and set the new size (dispose of the existing one if any)
            if (PictureBufferLength != length)
            {
                if (PictureBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(PictureBuffer);

                PictureBufferLength = length;
                PictureBuffer = Marshal.AllocHGlobal(PictureBufferLength);
            }

            return PictureBuffer;
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);
            if (Scaler != null)
                ffmpeg.sws_freeContext(Scaler);

            if (PictureBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(PictureBuffer);
        }

        #endregion
    }

    /// <summary>
    /// Performs audio sample decoding, scaling and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class AudioComponent : MediaComponent
    {
        #region Private Declarations

        /// <summary>
        /// Holds a reference to the audio resampler
        /// This resampler gets disposed upon disposal of this object.
        /// </summary>
        private SwrContext* Scaler = null;

        /// <summary>
        /// The audio samples buffer that has been allocated in unmanaged memory
        /// </summary>
        private IntPtr SamplesBuffer = IntPtr.Zero;

        /// <summary>
        /// The samples buffer length. This might differ from the decompressed,
        /// resampled data length that is obtained in the event. This represents a maximum
        /// allocated length.
        /// </summary>
        private int SamplesBufferLength;

        /// <summary>
        /// Used to determine if we have to reset the scaler parameters
        /// </summary>
        private AudioComponentSpec LastSourceSpec = null;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        internal AudioComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            // Placeholder. Nothing else to init.
        }

        #endregion

        #region Methods

        /// <summary>
        /// Allocates a buffer in inmanaged memory only if necessry.
        /// Returns the pointer to the allocated buffer regardless of it being new or existing.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private IntPtr AllocateBuffer(int length)
        {
            if (SamplesBufferLength < length)
            {
                if (SamplesBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(SamplesBuffer);

                SamplesBufferLength = length;
                SamplesBuffer = Marshal.AllocHGlobal(SamplesBufferLength);
            }

            return SamplesBuffer;
        }

        /// <summary>
        /// Processes the audio frame by resampling it and raising an even if there are any
        /// event subscribers.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            // Compute the timespans
            var renderTime = ffmpeg.av_frame_get_best_effort_timestamp(frame).ToTimeSpan(Stream->time_base);
            var duration = ffmpeg.av_frame_get_pkt_duration(frame).ToTimeSpan(Stream->time_base);

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastProcessedRenderTime = renderTime;

            // Check if there is a handler to feed the conversion to.
            if (Container.HandlesOnAudioDataAvailable == false)
                return;

            // Create the source and target ausio specs. We might need to scale from
            // the source to the target
            var sourceSpec = AudioComponentSpec.CreateSource(frame);
            var targetSpec = AudioComponentSpec.CreateTarget(frame);

            // Initialize or update the audio scaler if required
            if (Scaler == null || LastSourceSpec == null || AudioComponentSpec.AreCompatible(LastSourceSpec, sourceSpec) == false)
            {
                Scaler = ffmpeg.swr_alloc_set_opts(Scaler, targetSpec.ChannelLayout, targetSpec.Format, targetSpec.SampleRate,
                    sourceSpec.ChannelLayout, sourceSpec.Format, sourceSpec.SampleRate, 0, null);

                ffmpeg.swr_init(Scaler);
                LastSourceSpec = sourceSpec;
            }

            // Allocate the unmanaged output buffer
            var outputBuffer = AllocateBuffer(targetSpec.BufferLength);
            var outputBufferPtr = (byte*)outputBuffer;

            // Execute the conversion (audio scaling). It will return the number of samples that were output
            var outputSamplesPerChannel =
                ffmpeg.swr_convert(Scaler, &outputBufferPtr, targetSpec.SamplesPerChannel, frame->extended_data, frame->nb_samples);

            // Compute the buffer length
            var outputBufferLength =
                ffmpeg.av_samples_get_buffer_size(null, targetSpec.ChannelCount, outputSamplesPerChannel, targetSpec.Format, 1);

            // Send data to event subscribers
            Container.RaiseOnAudioDataAvailabe(outputBuffer, outputBufferLength,
                targetSpec.SampleRate, outputSamplesPerChannel, targetSpec.ChannelCount, renderTime, duration);

        }

        /// <summary>
        /// Processes the subtitle frame. This will throw if called.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        /// <exception cref="System.NotSupportedException"></exception>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            throw new NotSupportedException($"{nameof(AudioComponent)} does not support subtitle frame processing.");
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);

            if (Scaler != null)
                fixed (SwrContext** scaler = &Scaler)
                    ffmpeg.swr_free(scaler);

            if (SamplesBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(SamplesBuffer);
        }

        #endregion
    }

    /// <summary>
    /// Performs subtitle text decoding and extraction logic.
    /// </summary>
    /// <seealso cref="Unosquare.FFplayDotNet.MediaComponent" />
    public sealed unsafe class SubtitleComponent : MediaComponent
    {
        internal SubtitleComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {
            // placeholder. Nothing else to change here.
        }

        /// <summary>
        /// Processes the frame. If called, this will throw.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        /// <exception cref="System.NotSupportedException">SubtitleComponent</exception>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            throw new NotSupportedException($"{nameof(SubtitleComponent)} does not support processing of audio or video frames.");
        }

        /// <summary>
        /// Processes the subtitle frame. Only text subtitles are supported.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <param name="frame">The frame.</param>
        protected override unsafe void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            // Extract timing information
            var renderTime = frame->pts.ToTimeSpan();
            var startTime = renderTime + ((long)frame->start_display_time).ToTimeSpan(Stream->time_base);
            var endTime = renderTime + ((long)frame->end_display_time).ToTimeSpan(Stream->time_base);
            var duration = endTime - startTime;

            // Set the state
            LastProcessedTimeUTC = DateTime.UtcNow;
            LastProcessedRenderTime = renderTime;

            // Check if there is a handler to feed the conversion to.
            if (Container.HandlesOnSubtitleDataAvailable == false)
                return;

            // Extract text strings
            var subtitleText = new List<string>();

            for (var i = 0; i < frame->num_rects; i++)
            {
                var rect = frame->rects[i];
                if (rect->text != null)
                    subtitleText.Add(Native.BytePtrToStringUTF8(rect->text));

            }

            // Provide the data in an event
            Container.RaiseOnSubtitleDataAvailabe(subtitleText.ToArray(), startTime, endTime, duration);
        }
    }

    /// <summary>
    /// Represents a set of Audio, Video and Subtitle components.
    /// This class is useful in order to group all components into 
    /// a single set. Sending packets is automatically handled by
    /// this class. This class is not thread safe.
    /// </summary>
    public class MediaComponentSet
    {
        #region Private Declarations

        /// <summary>
        /// The internal Components
        /// </summary>
        protected readonly Dictionary<MediaType, MediaComponent> Items = new Dictionary<MediaType, MediaComponent>();

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaComponentSet"/> class.
        /// </summary>
        internal MediaComponentSet()
        {
            // prevent external initialization
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the video component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public VideoComponent Video
        {
            get { return Items.ContainsKey(MediaType.Video) ? Items[MediaType.Video] as VideoComponent : null; }
        }

        /// <summary>
        /// Gets the audio component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public AudioComponent Audio
        {
            get { return Items.ContainsKey(MediaType.Audio) ? Items[MediaType.Audio] as AudioComponent : null; }
        }

        /// <summary>
        /// Gets the subtitles component.
        /// Returns null when there is no such stream component.
        /// </summary>
        public SubtitleComponent Subtitles
        {
            get { return Items.ContainsKey(MediaType.Subtitle) ? Items[MediaType.Subtitle] as SubtitleComponent : null; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a video component.
        /// </summary>
        public bool HasVideo { get { return Video != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has an audio component.
        /// </summary>
        public bool HasAudio { get { return Audio != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has a subtitles component.
        /// </summary>
        public bool HasSubtitles { get { return Subtitles != null; } }

        /// <summary>
        /// Gets or sets the <see cref="MediaComponent"/> with the specified media type.
        /// Setting a new component on an existing media type component will throw.
        /// Getting a non existing media component fro the given media type will return null.
        /// </summary>
        /// <param name="mediaType">Type of the media.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException"></exception>
        /// <exception cref="System.ArgumentNullException">MediaComponent</exception>
        public MediaComponent this[MediaType mediaType]
        {
            get { return Items.ContainsKey(mediaType) ? Items[mediaType] : null; }
            set
            {
                if (Items.ContainsKey(mediaType))
                    throw new ArgumentException($"A component for '{mediaType}' is already registered.");

                Items[mediaType] = value ??
                    throw new ArgumentNullException($"{nameof(MediaComponent)} {nameof(value)} must not be null.");
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sends the specified packet to the correct component by reading the stream index
        /// of the packet that is being sent. No packet is sent if the provided packet is set to null.
        /// Returns true if the packet matched a component and was sent successfully. Otherwise, it returns false.
        /// </summary>
        /// <param name="packet">The packet.</param>
        /// <returns></returns>
        internal unsafe bool SendPacket(AVPacket* packet)
        {
            if (packet == null)
                return false;

            foreach (var item in Items)
            {
                if (item.Value.StreamIndex == packet->stream_index)
                {
                    item.Value.SendPacket(packet);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sends a flush packet to all media components.
        /// </summary>
        internal unsafe void SendFlushPacket()
        {
            foreach (var item in Items)
                item.Value.SendFlushPacket();
        }

        #endregion
    }

    /// <summary>
    /// Represents a set of options that are used to initialize a media container.
    /// </summary>
    public class MediaContainerOptions
    {
        // TODO: Support specific stream selection for each component, forced input format

        /// <summary>
        /// Gets or sets a value indicating whether [enable low resource].
        /// In theroy this should be 0,1,2,3 for 1, 1/2, 1,4 and 1/8 resolutions.
        /// TODO: We are for now just supporting 1/2 rest (true value)
        /// Port of lowres.
        /// </summary>
        public bool EnableLowRes { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether [enable fast decoding].
        /// Port of fast
        /// </summary>
        public bool EnableFastDecoding { get; set; } = false;

        /// <summary>
        /// A dictionary of Format options.
        /// Supported format options are specified in https://www.ffmpeg.org/ffmpeg-formats.html#Format-Options
        /// </summary>
        public Dictionary<string, string> FormatOptions { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Gets the codec options.
        /// Codec options are documented here: https://www.ffmpeg.org/ffmpeg-codecs.html#Codec-Options
        /// Port of codec_opts
        /// </summary>
        public OptionsCollection CodecOptions { get; } = new OptionsCollection();

        /// <summary>
        /// Gets or sets a value indicating whether PTS are generated automatically and not read
        /// from the packets themselves. Defaults to false.
        /// Port of genpts
        /// </summary>
        public bool GeneratePts { get; set; } = false;

        /// <summary>
        /// Prevent reading from audio stream components.
        /// Port of audio_disable
        /// </summary>
        public bool IsAudioDisabled { get; set; } = false;

        /// <summary>
        /// Prevent reading from video stream components.
        /// Port of video_disable
        /// </summary>
        public bool IsVideoDisabled { get; set; } = false;

        /// <summary>
        /// Prevent reading from subtitle stream components.
        /// Port of subtitle_disable
        /// </summary>
        public bool IsSubtitleDisabled { get; set; } = false;
    }

    /// <summary>
    /// A container capable of opening an input url,
    /// reading packets from it, decoding frames, seeking, and pausing and resuming network streams
    /// Code heavily based on https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe class MediaContainer : IDisposable
    {
        // TODO: Seeking and resetting attached picture

        #region Constants

        private static class EntryName
        {
            public const string ScanAllPMTs = "scan_all_pmts";
            public const string Title = "title";
        }

        #endregion

        #region Private Fields

        /// <summary>
        /// Holds a reference to an input context.
        /// </summary>
        internal AVFormatContext* InputContext = null;

        /// <summary>
        /// The initialization options.
        /// </summary>
        internal MediaContainerOptions Options = null;

        /// <summary>
        /// Determines if the stream seeks by bytes always
        /// </summary>
        private bool SeekByBytes = false;

        private bool m_RequiresPictureAttachments = true;

        private readonly object SyncRoot = new object();

        private bool IsDisposing = false;

        private readonly MediaActionQueue ActionQueue = new MediaActionQueue();

        #endregion

        #region Events

        /// <summary>
        /// Occurs when video data is available.
        /// </summary>
        public event EventHandler<VideoDataAvailableEventArgs> OnVideoDataAvailable;

        /// <summary>
        /// Occurs when audio data is available.
        /// </summary>
        public event EventHandler<AudioDataAvailableEventArgs> OnAudioDataAvailable;

        /// <summary>
        /// Occurs when subtitle data is available.
        /// </summary>
        public event EventHandler<SubtitleDataAvailableEventArgs> OnSubtitleDataAvailable;

        /// <summary>
        /// Gets a value indicating whether the event is bound
        /// </summary>
        internal bool HandlesOnVideoDataAvailable { get { return OnVideoDataAvailable != null; } }

        /// <summary>
        /// Gets a value indicating whether the event is bound
        /// </summary>
        internal bool HandlesOnAudioDataAvailable { get { return OnAudioDataAvailable != null; } }

        /// <summary>
        /// Gets a value indicating whether the event is bound
        /// </summary>
        internal bool HandlesOnSubtitleDataAvailable { get { return OnSubtitleDataAvailable != null; } }

        /// <summary>
        /// Raises the on video data availabe.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="bufferStride">The buffer stride.</param>
        /// <param name="pixelWidth">Width of the pixel.</param>
        /// <param name="pixelHeight">Height of the pixel.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnVideoDataAvailabe(IntPtr buffer, int bufferLength, int bufferStride,
            int pixelWidth, int pixelHeight, TimeSpan renderTime, TimeSpan duration)
        {
            if (HandlesOnVideoDataAvailable == false) return;
            OnVideoDataAvailable(this, new VideoDataAvailableEventArgs(buffer, bufferLength, bufferStride,
                pixelWidth, pixelHeight, renderTime, duration));
        }

        /// <summary>
        /// Raises the on audio data availabe.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="sampleRate">The sample rate.</param>
        /// <param name="samplesPerChannel">The samples per channel.</param>
        /// <param name="channels">The channels.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnAudioDataAvailabe(IntPtr buffer, int bufferLength,
            int sampleRate, int samplesPerChannel, int channels, TimeSpan renderTime, TimeSpan duration)
        {
            if (HandlesOnAudioDataAvailable == false) return;
            OnAudioDataAvailable(this, new AudioDataAvailableEventArgs(buffer, bufferLength, sampleRate,
            samplesPerChannel, channels, renderTime, duration));
        }

        /// <summary>
        /// Raises the on subtitle data availabe.
        /// </summary>
        /// <param name="textLines">The text lines.</param>
        /// <param name="renderTime">The render time.</param>
        /// <param name="endTime">The end time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnSubtitleDataAvailabe(string[] textLines, TimeSpan renderTime, TimeSpan endTime, TimeSpan duration)
        {
            if (HandlesOnSubtitleDataAvailable == false) return;
            OnSubtitleDataAvailable(this, new SubtitleDataAvailableEventArgs(textLines, renderTime, endTime, duration));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the media URL. This is the input url, file or device that is read
        /// by this container.
        /// </summary>
        public string MediaUrl { get; private set; }

        /// <summary>
        /// Gets the name of the media format.
        /// </summary>
        public string MediaFormatName { get; private set; }

        /// <summary>
        /// If available, the title will be extracted from the metadata of the media.
        /// Otherwise, this will be set to false.
        /// </summary>
        public string MediaTitle { get; private set; }

        /// <summary>
        /// Will be set to true whenever an End Of File situation is reached.
        /// </summary>
        public bool IsAtEndOfFile { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this container represents realtime media.
        /// If the format name is rtp, rtsp, or sdp or if the url starts with udp: or rtp:
        /// then this property will be set to true.
        /// </summary>
        public bool IsMediaRealtime { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a packet read delay witll be enforced.
        /// RSTP formats of MMSH Urls will have this property set to true.
        /// Reading packets will block for 10 or less milliseconds depending on the last read time.
        /// </summary>
        public bool RequiresPacketReadDelay { get; private set; }

        /// <summary>
        /// Gets the time the last packet was read from the input
        /// </summary>
        public DateTime LastPacketReadTimeUtc { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// Gets the media start time. It could be something other than 0.
        /// If this start time is not available (i.e. realtime streams) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaStartTime { get; private set; }

        /// <summary>
        /// Gets the duration of the media.
        /// If this information is not available (i.e. realtime streams) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaDuration { get; private set; }

        /// <summary>
        /// Gets the end time of the media.
        /// If this information is not available (i.e. realtime streams) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaEndTime { get; private set; }

        /// <summary>
        /// Provides direct access to the individual Media components of the input stream.
        /// </summary>
        public MediaComponentSet Components { get; }

        private bool RequiresPictureAttachments
        {
            get
            {
                var canRequireAttachments = Components.HasVideo
                    && (Components.Video.Stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0;

                if (canRequireAttachments == false)
                    return false;
                else
                    return m_RequiresPictureAttachments;
            }
            set
            {
                var canRequireAttachments = Components.HasVideo
                    && (Components.Video.Stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0;

                if (canRequireAttachments)
                    m_RequiresPictureAttachments = value;
                else
                    m_RequiresPictureAttachments = false;
            }
        }


        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaContainer"/> class.
        /// </summary>
        /// <param name="mediaUrl">The media URL.</param>
        /// <param name="forcedFormatName">Name of the format.</param>
        /// <exception cref="System.ArgumentNullException">mediaUrl</exception>
        /// <exception cref="Unosquare.FFplayDotNet.MediaContainerException"></exception>
        public MediaContainer(string mediaUrl, string forcedFormatName = null)
        {
            // Argument Validation
            if (string.IsNullOrWhiteSpace(mediaUrl))
                throw new ArgumentNullException($"{nameof(mediaUrl)}");

            // Initialize the library (if not already done)
            Helper.RegisterFFmpeg();

            // Create the options object
            MediaUrl = mediaUrl;
            Options = new MediaContainerOptions();

            // Retrieve the input format (null = auto for default)
            AVInputFormat* inputFormat = null;
            if (string.IsNullOrWhiteSpace(forcedFormatName) == false)
            {
                inputFormat = ffmpeg.av_find_input_format(forcedFormatName);
                $"Format '{forcedFormatName}' not found. Will use automatic format detection.".Warn(typeof(MediaContainer));
            }

            try
            {
                // Create the input format context, and open the input based on the provided format options.
                using (var formatOptions = new FFDictionary(Options.FormatOptions))
                {
                    if (formatOptions.HasKey(EntryName.ScanAllPMTs) == false)
                        formatOptions.Set(EntryName.ScanAllPMTs, "1", true);

                    // Allocate the input context and save it
                    var inputContext = ffmpeg.avformat_alloc_context();
                    InputContext = inputContext; // we save the InputContext as it will be used by other funtions (including dispose)

                    // Open the input file
                    var openResult = ffmpeg.avformat_open_input(&inputContext, MediaUrl, inputFormat, formatOptions.Reference);

                    // Validate the open operation
                    if (openResult < 0) throw new MediaContainerException($"Could not open '{MediaUrl}'. Error code: {openResult}");

                    // Set some general properties
                    MediaFormatName = Native.BytePtrToString(InputContext->iformat->name);

                    // If there are any optins left in the dictionary, it means they dod not get used (invalid options).
                    formatOptions.Remove(EntryName.ScanAllPMTs);
                    if (formatOptions.First() != null)
                        $"Invalid format option: '{formatOptions.First()?.Key}'".Warn(typeof(MediaContainer));
                }

                // Inject Codec Parameters
                if (Options.GeneratePts) InputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
                ffmpeg.av_format_inject_global_side_data(InputContext);

                // This is useful for file formats with no headers such as MPEG. This function also computes the real framerate in case of MPEG-2 repeat frame mode.
                if (ffmpeg.avformat_find_stream_info(InputContext, null) < 0)
                    $"{MediaUrl}: could read stream info.".Warn(typeof(MediaContainer));

                // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end
                if (InputContext->pb != null) InputContext->pb->eof_reached = 0;

                // Setup initial state variables
                MediaTitle = FFDictionary.GetEntry(InputContext->metadata, EntryName.Title, false)?.Value;
                IsMediaRealtime = new[] { "rtp", "rtsp", "sdp" }.Any(s => MediaFormatName.Equals(s)) ||
                    (InputContext->pb != null && new[] { "rtp:", "udp:" }.Any(s => MediaUrl.StartsWith(s)));

                RequiresPacketReadDelay = MediaFormatName.Equals("rstp") || MediaUrl.StartsWith("mmsh:");
                var inputAllowsDiscontinuities = (InputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;
                SeekByBytes = inputAllowsDiscontinuities && (MediaFormatName.Equals("ogg") == false);

                // Compute timespans
                MediaStartTime = InputContext->start_time.ToTimeSpan();
                MediaDuration = InputContext->duration.ToTimeSpan();

                if (MediaStartTime != TimeSpan.MinValue && MediaDuration != TimeSpan.MinValue)
                    MediaEndTime = MediaStartTime + MediaDuration;
                else
                    MediaEndTime = TimeSpan.MinValue;

                //SeekToStartTimestamp();

                // Open the best suitable streams. Throw if no audio and/or video streams are found
                Components = CreateStreamComponents();

                // Initially and depending on the video component, rquire picture attachments.
                // Picture attachments are only required after the first read or after a seek.
                RequiresPictureAttachments = true;

                // for realtime streams
                if (IsMediaRealtime)
                    ffmpeg.av_read_play(InputContext);
            }
            catch (Exception ex)
            {
                $"Fatal error initializing {nameof(MediaContainer)} instance. {ex.Message}".Error(typeof(MediaContainer));
                Dispose(true);
                throw;
            }
        }

        /// <summary>
        /// Creates the stream components by first finding the best available streams.
        /// Then it initializes the components of the correct type each.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Unosquare.FFplayDotNet.MediaContainerException"></exception>
        private MediaComponentSet CreateStreamComponents()
        {
            // Display stream information in the console if we are debugging
            if (Debugger.IsAttached)
                ffmpeg.av_dump_format(InputContext, 0, MediaUrl, 0);

            // Initialize and clear all the stream indexes.
            var streamIndexes = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            for (var i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
                streamIndexes[i] = -1;

            { // Find best streams for each component

                // if we passed null instead of the requestedCodec pointer, then
                // find_best_stream would not validate whether a valid decoder is registed.
                AVCodec* requestedCodec = null;

                if (Options.IsVideoDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                        ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1,
                                            &requestedCodec, 0);

                if (Options.IsAudioDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        &requestedCodec, 0);

                if (Options.IsSubtitleDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                    ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                        (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                        &requestedCodec, 0);
            }

            var result = new MediaComponentSet();
            var allMediaTypes = Enum.GetValues(typeof(MediaType));

            foreach (var mediaTypeItem in allMediaTypes)
            {
                var mediaType = (MediaType)mediaTypeItem;

                try
                {
                    if (streamIndexes[(int)mediaType] >= 0)
                    {
                        switch (mediaType)
                        {
                            case MediaType.Video:
                                result[mediaType] = new VideoComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            case MediaType.Audio:
                                result[mediaType] = new AudioComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            case MediaType.Subtitle:
                                result[mediaType] = new SubtitleComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            default:
                                continue;
                        }

                        if (Debugger.IsAttached)
                            $"{mediaType}: Selected Stream Index = {result[mediaType].StreamIndex}".Info(typeof(MediaContainer));
                    }

                }
                catch (Exception ex)
                {
                    $"Unable to initialize {mediaType.ToString()} component. {ex.Message}".Error(typeof(MediaContainer));
                }
            }


            // Verify we have at least 1 valid stream component to work with.
            if (result.HasVideo == false && result.HasAudio == false)
                throw new MediaContainerException($"{MediaUrl}: No audio or video streams found to decode.");

            return result;

        }

        public void Process()
        {
            lock (SyncRoot)
            {
                if (ActionQueue.Count == 0)
                {
                    try
                    {
                        Read();
                    }
                    catch (Exception ex)
                    {
                        $"{ex.Message}".Error(typeof(MediaContainer));
                    }
                }

            }

        }

        private void Read()
        {
            if (RequiresPacketReadDelay)
            {
                // in ffplay.c this is referenced via CONFIG_RTSP_DEMUXER || CONFIG_MMSH_PROTOCOL
                var millisecondsDifference = (int)Math.Round(DateTime.UtcNow.Subtract(LastPacketReadTimeUtc).TotalMilliseconds, 2);
                var sleepMilliseconds = 10 - millisecondsDifference;

                // wait at least 10 ms to avoid trying to get another packet
                if (sleepMilliseconds > 0)
                    Thread.Sleep(sleepMilliseconds); // XXX: horrible
            }

            if (RequiresPictureAttachments)
            {
                var attachedPacket = ffmpeg.av_packet_alloc();
                var copyPacketResult = ffmpeg.av_copy_packet(attachedPacket, &Components.Video.Stream->attached_pic);
                if (copyPacketResult >= 0 && attachedPacket != null)
                {
                    Components.Video.SendPacket(attachedPacket);
                    Components.Video.SendFlushPacket();
                }

                RequiresPictureAttachments = false;
            }

            // Allocate the packet to read
            var readPacket = ffmpeg.av_packet_alloc();
            var readResult = ffmpeg.av_read_frame(InputContext, readPacket);
            LastPacketReadTimeUtc = DateTime.UtcNow;

            if (readResult < 0)
            {
                // Handle failed packet reads. We don't need the allocated packet anymore
                ffmpeg.av_packet_free(&readPacket);

                // Detect an end of file situation (makes the readers enter draining mode)
                if ((readResult == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(InputContext->pb) != 0) && IsAtEndOfFile == false)
                {
                    Components.SendFlushPacket();
                    IsAtEndOfFile = true;
                    return;
                }

                if (IsAtEndOfFile == false && InputContext->pb != null && InputContext->pb->error != 0)
                    throw new MediaContainerException($"Input has produced an error. Error Code {InputContext->pb->error}");
            }
            else
            {
                IsAtEndOfFile = false;
            }

            // Check if we were able to feed the packet. If not, simply discard it
            if (readPacket != null && Components.SendPacket(readPacket) == false)
                ffmpeg.av_packet_free(&readPacket);
        }

        #region IDisposable Support

        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                if (alsoManaged)
                {
                    if (InputContext != null)
                    {
                        fixed (AVFormatContext** inputContext = &InputContext)
                            ffmpeg.avformat_close_input(inputContext);

                        ffmpeg.avformat_free_context(InputContext);
                    }
                }

                IsDisposing = true;
            }
        }

        ~MediaContainer()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
