using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Unosquare.FFplayDotNet.Core;
using Unosquare.FFplayDotNet.Primitives;
using Unosquare.Swan;

namespace Unosquare.FFplayDotNet
{
    internal static class MediaUtils
    {
        public static TimeSpan GetTimeSpan(double pts, AVRational timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.MinValue;

            if (timeBase.den == 0)
                return TimeSpan.FromSeconds(pts / ffmpeg.AV_TIME_BASE);

            return TimeSpan.FromSeconds(pts * timeBase.num / timeBase.den);
        }

        public static TimeSpan GetTimeSpan(double pts, double timeBase)
        {
            if (double.IsNaN(pts) || pts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.MinValue;

            return TimeSpan.FromSeconds(pts * 1d / timeBase);
        }
    }

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
    /// A data structure containing a quque of packets to process.
    /// This class is thread safe and disposable.
    /// Enqueued, unmanaged packets are disposed automatically by this queue.
    /// Dequeued packets are the responsibility of the calling code.
    /// </summary>
    public unsafe class MediaPacketQueue : IDisposable
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
    /// Represents a media component of a given media type within a 
    /// media container. Derived classes must implement frame handling
    /// logic.
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe abstract class MediaComponent : IDisposable
    {

        /// <summary>
        /// Contains constants defining dictionary entry names for codec options
        /// </summary>
        protected static class CodecOption
        {
            public const string Threads = "threads";
            public const string RefCountedFrames = "refcounted_frames";
            public const string LowRes = "lowres";
        }


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
        protected readonly MediaPacketQueue Packets = new MediaPacketQueue();

        /// <summary>
        /// The packets that have been sent to the decoder. We keep track of them in order to dispose them
        /// once a frame has been decoded.
        /// </summary>
        protected readonly MediaPacketQueue SentPackets = new MediaPacketQueue();

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

            // Setup additional settings. The most important one is Threads -- 
            // It allows decoding to occur multi-threaded (much faster in most scenarios)
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

            // Display stream information in the console if we are debugging
            if (Debugger.IsAttached)
                ffmpeg.av_dump_format(container.InputContext, 0, container.MediaUrl, 0);

            // Startup done. Set some options.
            Stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            MediaType = (MediaType)CodecContext->codec_type;

            // Compute the start time
            if (Stream->start_time == ffmpeg.AV_NOPTS_VALUE)
                StartTime = MediaUtils.GetTimeSpan(Container.InputContext->start_time, ffmpeg.AV_TIME_BASE);
            else
                StartTime = MediaUtils.GetTimeSpan(Stream->start_time, Stream->time_base);

            // compute the duration
            if (Stream->duration == ffmpeg.AV_NOPTS_VALUE || Stream->duration == 0)
                Duration = MediaUtils.GetTimeSpan(Container.InputContext->duration, ffmpeg.AV_TIME_BASE);
            else
                Duration = MediaUtils.GetTimeSpan(Stream->duration, Stream->time_base);

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
        /// Pusches a packet into the decoding Packet Queue
        /// and processes the packet in order to try to decode
        /// 1 or more frames. The packet has to be within the range of
        /// the start time and end time of 
        /// </summary>
        /// <param name="packet">The packet.</param>
        public void SendPacket(AVPacket* packet)
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
                    while (gotFrame != 0 || receiveFrameResult >= 0)
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

        protected virtual void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            $"{MediaType}: Processing Frame from packet ({packet->pos}). PTS: {MediaUtils.GetTimeSpan(frame->pts, Stream->time_base)}".Trace(typeof(MediaContainer));
        }

        protected virtual void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            $"{MediaType}: Processing Frame from packet ({packet->pos}). PTS: {MediaUtils.GetTimeSpan(frame->pts, Stream->time_base)}".Trace(typeof(MediaContainer));
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

    public unsafe class VideoComponent : MediaComponent
    {
        private SwsContext* Scaler = null;
        private IntPtr PictureBuffer;
        private int PictureBufferLength;
        private byte[] ManagedBuffer;
        private WriteableBitmap OutputBitmap;

        public VideoComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {

        }

        protected override void ProcessFrame(AVPacket* packet, AVFrame* frame)
        {
            base.ProcessFrame(packet, frame);

            // for vide frames, we always get the best effort timestamp as dts and pts might
            // contain different times.
            frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);

            // Retrieve a suitable scaler or create it on the fly
            Scaler = ffmpeg.sws_getCachedContext(Scaler,
                    frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height,
                    Constants.OutputPixelFormat, Container.Options.VideoScalerFlags, null, null, null);

            var bitmap = UpdateBitmap(frame);
            var currentSeconds = Math.Round(frame->pts * ffmpeg.av_q2d(Stream->time_base), 0);
            var outputFilename = $"c:\\users\\unosp\\desktop\\output\\test-{currentSeconds:000}.png";

            if (File.Exists(outputFilename) == false)
                SaveBitmapToPng(outputFilename);
        }

        private IntPtr AllocateBuffer(int length)
        {
            if (PictureBufferLength != length)
            {
                if (PictureBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(PictureBuffer);

                PictureBufferLength = length;
                PictureBuffer = Marshal.AllocHGlobal(PictureBufferLength);
                ManagedBuffer = new byte[PictureBufferLength];
            }

            return PictureBuffer;
        }

        private byte[] UpdateBitmapBuffer(AVFrame* frame)
        {
            var targetStride = new int[] {
                ffmpeg.av_image_get_linesize(Constants.OutputPixelFormat, frame->width, 0)
            };

            var targetLength = ffmpeg.av_image_get_buffer_size(Constants.OutputPixelFormat, frame->width, frame->height, 1);
            var targetScan = new byte_ptrArray8();

            var unmanagedBuffer = AllocateBuffer(targetLength);
            targetScan[0] = (byte*)unmanagedBuffer;

            var outputHeight = ffmpeg.sws_scale(Scaler, frame->data, frame->linesize, 0, frame->height, targetScan, targetStride);
            Marshal.Copy(unmanagedBuffer, ManagedBuffer, 0, PictureBufferLength);

            return ManagedBuffer;
        }

        private void SaveBitmapToPng(string filename)
        {
            using (var fileStream = new FileStream(filename, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(OutputBitmap));
                encoder.Save(fileStream);
            }
        }

        private WriteableBitmap UpdateBitmap(AVFrame* frame)
        {
            var targetStride = new int[] {
                ffmpeg.av_image_get_linesize(Constants.OutputPixelFormat, frame->width, 0)
            };

            var targetLength = ffmpeg.av_image_get_buffer_size(Constants.OutputPixelFormat, frame->width, frame->height, 1);
            var targetScan = new byte_ptrArray8();

            if (OutputBitmap == null || OutputBitmap.PixelWidth != frame->width || OutputBitmap.PixelHeight != frame->height)
                OutputBitmap = new WriteableBitmap(frame->width, frame->height, 96, 96, PixelFormats.Bgr24, null);

            OutputBitmap.Lock();
            targetScan[0] = (byte*)OutputBitmap.BackBuffer;
            var outputHeight = ffmpeg.sws_scale(Scaler, frame->data, frame->linesize, 0, frame->height, targetScan, targetStride);
            OutputBitmap.AddDirtyRect(new Int32Rect(0, 0, OutputBitmap.PixelWidth, OutputBitmap.PixelHeight));
            OutputBitmap.Unlock();

            return OutputBitmap;
        }

        protected override void ProcessFrame(AVPacket* packet, AVSubtitle* frame)
        {
            throw new NotSupportedException("This stream component reader does not support subtitles.");
        }

        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);
            if (Scaler != null)
                ffmpeg.sws_freeContext(Scaler);

            if (PictureBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(PictureBuffer);
        }
    }

    public unsafe class AudioComponent : MediaComponent
    {
        public AudioComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {

        }
    }

    public unsafe class SubtitleComponent : MediaComponent
    {
        public SubtitleComponent(MediaContainer container, int streamIndex)
            : base(container, streamIndex)
        {

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
        /// </summary>
        public VideoComponent Video
        {
            get { return Items.ContainsKey(MediaType.Video) ? Items[MediaType.Video] as VideoComponent : null; }
        }

        /// <summary>
        /// Gets the audio component.
        /// </summary>
        public AudioComponent Audio
        {
            get { return Items.ContainsKey(MediaType.Audio) ? Items[MediaType.Audio] as AudioComponent : null; }
        }

        /// <summary>
        /// Gets the subtitles component.
        /// </summary>
        public SubtitleComponent Subtitles
        {
            get { return Items.ContainsKey(MediaType.Subtitle) ? Items[MediaType.Subtitle] as SubtitleComponent : null; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has a video component.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance has video; otherwise, <c>false</c>.
        /// </value>
        public bool HasVideo { get { return Video != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has an audio component.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance has audio; otherwise, <c>false</c>.
        /// </value>
        public bool HasAudio { get { return Audio != null; } }

        /// <summary>
        /// Gets a value indicating whether this instance has a subtitles component.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance has subtitles; otherwise, <c>false</c>.
        /// </value>
        public bool HasSubtitles { get { return Subtitles != null; } }

        /// <summary>
        /// Gets or sets the <see cref="MediaComponent"/> with the specified media type.
        /// </summary>
        /// <value>
        /// The <see cref="MediaComponent"/>.
        /// </value>
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
        /// Sends an empty packet to all media components.
        /// </summary>
        internal unsafe void SendFlushPacket()
        {
            foreach (var item in Items)
                item.Value.SendFlushPacket();
        }

        #endregion
    }

    /// <summary>
    /// A container capable of opening an input url,
    /// reading packets from it, decoding frames, pausing and resuming network streams
    /// Code heavily based on https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe class MediaContainer : IDisposable
    {
        #region Constants

        protected static class EntryName
        {
            public const string ScanAllPMTs = "scan_all_pmts";
            public const string Title = "title";
        }

        #endregion

        #region Private Fields

        internal AVFormatContext* InputContext = null;
        internal PlayerOptions Options = null;

        private readonly object SyncRoot = new object();
        private bool IsDisposing = false;
        private bool SeekByBytes = false;
        private bool EnableInfiniteBuffer = false;
        private bool InputAllowsDiscontinuities = false;

        private readonly MediaActionQueue ActionQueue = new MediaActionQueue();


        #endregion

        #region Properties

        public string MediaUrl { get; private set; }

        public string MediaTitle
        {
            get
            {
                if (InputContext == null) return null;
                var optionEntry = FFDictionary.GetEntry(InputContext->metadata, EntryName.Title, false);
                return optionEntry?.Value;
            }
        }

        public bool IsAtEndOfFile { get; private set; }

        public string InputFormatName { get; private set; }

        public bool IsMediaRealtime { get; private set; }

        public double MediaStartTime { get; private set; }

        public double MediaDuration { get; private set; }

        public MediaComponentSet Components { get; }

        public int DecodedVideoFrames
        {
            get
            {
                lock (SyncRoot)
                    return Components.Video?.DecodedFrameCount ?? 0;
            }
        }

        public double Framerate
        {
            get
            {
                lock (SyncRoot)
                    return InputContext != null && Components.Video != null ?
                        ffmpeg.av_q2d(Components.Video.Stream->r_frame_rate) : 0;
            }
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaContainer"/> class.
        /// </summary>
        /// <param name="mediaUrl">The media URL.</param>
        /// <param name="formatName">Name of the format.</param>
        /// <exception cref="System.ArgumentNullException">mediaUrl</exception>
        /// <exception cref="Unosquare.FFplayDotNet.MediaContainerException"></exception>
        public MediaContainer(string mediaUrl, string formatName = null)
        {

            // Argument Validation
            if (string.IsNullOrWhiteSpace(mediaUrl))
                throw new ArgumentNullException($"{nameof(mediaUrl)}");

            // Initialize the library (if not already done)
            Helper.RegisterFFmpeg();

            // Create the options object
            MediaUrl = mediaUrl;
            Options = new PlayerOptions();

            // Retrieve the input format (null = auto for default)
            AVInputFormat* inputFormat = null;
            if (string.IsNullOrWhiteSpace(formatName) == false)
            {
                inputFormat = ffmpeg.av_find_input_format(formatName);
                $"Format '{formatName}' not found. Will use automatic format detection.".Warn(typeof(MediaContainer));
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
                    InputFormatName = Native.BytePtrToString(InputContext->iformat->name);

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
                IsMediaRealtime = new[] { "rtp", "rtsp", "sdp" }.Any(s => InputFormatName.Equals(s)) ||
                    (InputContext->pb != null && new[] { "rtp:", "udp:" }.Any(s => MediaUrl.StartsWith(s)));
                InputAllowsDiscontinuities = (InputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;
                EnableInfiniteBuffer = IsMediaRealtime;
                SeekByBytes = InputAllowsDiscontinuities && (InputFormatName.Equals("ogg") == false);

                MediaStartTime = (double)InputContext->start_time / ffmpeg.AV_TIME_BASE;
                MediaDuration = (double)InputContext->duration / ffmpeg.AV_TIME_BASE;

                //SeekToStartTimestamp();

                // Open the best suitable streams. Throw if no audio and/or video streams are found
                Components = CreateStreamComponents();

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

        private MediaComponentSet CreateStreamComponents()
        {

            var streamIndexes = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            for (var i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
                streamIndexes[i] = -1;

            { // Find best streams for each component

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

            try
            {
                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
                    result[MediaType.Video] = new VideoComponent(this, streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]);
            }
            catch (Exception ex)
            {
                $"Unable to initialize {MediaType.Video.ToString()} component. {ex.Message}".Error(typeof(MediaContainer));
            }

            try
            {
                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
                    result[MediaType.Audio] = new AudioComponent(this, streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]);
            }
            catch (Exception ex)
            {
                $"Unable to initialize {MediaType.Audio.ToString()} component. {ex.Message}".Error(typeof(MediaContainer));
            }

            try
            {
                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
                    result[MediaType.Subtitle] = new SubtitleComponent(this, streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE]);
            }
            catch (Exception ex)
            {
                $"Unable to initialize {MediaType.Subtitle.ToString()} component. {ex.Message}".Error(typeof(MediaContainer));
            }

            if (result.HasVideo == false && result.HasAudio == false)
                throw new MediaContainerException($"{MediaUrl}: No audio or video streams found to decode.");

            return result;

        }

        private void SeekToStartTimestamp()
        {
            if (InputContext->start_time == ffmpeg.AV_NOPTS_VALUE)
                return;

            var seekToStartResult = ffmpeg.avformat_seek_file(InputContext, -1, long.MinValue, InputContext->start_time, long.MaxValue, 0);

            if (seekToStartResult < 0)
                $"File '{MediaUrl}'. Could not seek to position {(double)InputContext->start_time / ffmpeg.AV_TIME_BASE} secs.".Warn(typeof(MediaContainer));

        }

        public void PushAction(MediaAction action)
        {
            lock (SyncRoot)
            {
                ActionQueue.Push(null, action);
            }

        }

        public void Process()
        {
            lock (SyncRoot)
            {
                if (ActionQueue.Count == 0)
                    Read();
            }

        }

        private void Read()
        {
            // Allocate the packet to read
            var readPacket = ffmpeg.av_packet_alloc();
            var readResult = ffmpeg.av_read_frame(InputContext, readPacket);

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
            if (Components.SendPacket(readPacket) == false)
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
