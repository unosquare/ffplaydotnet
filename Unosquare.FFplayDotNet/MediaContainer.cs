namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.Swan;

    /// <summary>
    /// A container capable of opening an input url,
    /// reading packets from it, decoding frames, seeking, and pausing and resuming network streams
    /// Code heavily based on https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe class MediaContainer : IDisposable
    {

        #region Constants

        private static class EntryName
        {
            public const string ScanAllPMTs = "scan_all_pmts";
            public const string Title = "title";
        }

        #endregion

        #region Private Fields

        /// <summary>
        /// To detect redundat Dispose calls
        /// </summary>
        private bool IsDisposed = false;

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
        internal bool MediaSeeksByBytes = false;

        /// <summary>
        /// Hold the value for the internal property with the same name.
        /// Picture attachments are required when video streams support them
        /// and these attached packets must be read before reading the first frame
        /// of the stream and after seeking.
        /// </summary>
        private bool m_RequiresPictureAttachments = true;

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
        /// <param name="startTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnVideoDataAvailabe(IntPtr buffer, int bufferLength, int bufferStride,
            int pixelWidth, int pixelHeight, TimeSpan startTime, TimeSpan duration)
        {
            if (HandlesOnVideoDataAvailable == false) return;

            //CurrentDispatcher.Invoke(() =>
            //{
            OnVideoDataAvailable(this, new VideoDataAvailableEventArgs(buffer, bufferLength, bufferStride,
                pixelWidth, pixelHeight, startTime, duration));
            //}, DispatcherPriority.DataBind);

        }

        /// <summary>
        /// Raises the on audio data availabe.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="bufferLength">Length of the buffer.</param>
        /// <param name="sampleRate">The sample rate.</param>
        /// <param name="samplesPerChannel">The samples per channel.</param>
        /// <param name="channels">The channels.</param>
        /// <param name="startTime">The render time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnAudioDataAvailabe(IntPtr buffer, int bufferLength,
            int sampleRate, int samplesPerChannel, int channels, TimeSpan startTime, TimeSpan duration)
        {
            if (HandlesOnAudioDataAvailable == false) return;
            //CurrentDispatcher.Invoke(() =>
            //{
            OnAudioDataAvailable(this, new AudioDataAvailableEventArgs(buffer, bufferLength, sampleRate,
                samplesPerChannel, channels, startTime, duration));
            //}, DispatcherPriority.DataBind);
        }

        /// <summary>
        /// Raises the on subtitle data availabe.
        /// </summary>
        /// <param name="textLines">The text lines.</param>
        /// <param name="startTime">The render time.</param>
        /// <param name="endTime">The end time.</param>
        /// <param name="duration">The duration.</param>
        internal void RaiseOnSubtitleDataAvailabe(string[] textLines, TimeSpan startTime, TimeSpan endTime, TimeSpan duration)
        {
            if (HandlesOnSubtitleDataAvailable == false) return;
            //CurrentDispatcher.Invoke(() =>
            //{
            OnSubtitleDataAvailable(this, new SubtitleDataAvailableEventArgs(textLines, startTime, endTime, duration));
            //}, DispatcherPriority.DataBind);
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
        /// Gets the media bitrate. Returns 0 if not available.
        /// </summary>
        public long MediaBitrate
        {
            get
            {
                if (InputContext == null) return 0;
                return InputContext->bit_rate;
            }
        }

        /// <summary>
        /// If available, the title will be extracted from the metadata of the media.
        /// Otherwise, this will be set to false.
        /// </summary>
        public string MediaTitle { get; private set; }

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
        /// Will be set to true whenever an End Of File situation is reached.
        /// </summary>
        public bool IsAtEndOfStream { get; private set; }


        /// <summary>
        /// Gets the byte position at which the stream is being read.
        /// </summary>
        public long StreamPosition
        {
            get
            {
                if (InputContext == null) return 0;
                return ffmpeg.avio_tell(InputContext->pb);
            }
        }

        /// <summary>
        /// Gets a value indicating whether the underlying media is seekable.
        /// </summary>
        public bool IsStreamSeekable { get { return MediaDuration.TotalSeconds > 0; } }

        /// <summary>
        /// Gets a value indicating whether this container represents realtime media.
        /// If the format name is rtp, rtsp, or sdp or if the url starts with udp: or rtp:
        /// then this property will be set to true.
        /// </summary>
        public bool IsRealtimeStream { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a packet read delay witll be enforced.
        /// RSTP formats or MMSH Urls will have this property set to true.
        /// Reading packets will block for at most 10 milliseconds depending on the last read time.
        /// This is a hack according to the source code in ffplay.c
        /// </summary>
        public bool RequiresReadDelay { get; private set; }

        /// <summary>
        /// Gets the time the last packet was read from the input
        /// </summary>
        public DateTime LastReadTimeUtc { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// For RTSP and other realtime streams reads can be suspended.
        /// </summary>
        public bool CanReadSuspend { get; private set; }

        /// <summary>
        /// For RTSP and other realtime streams reads can be suspended.
        /// This property will return true if reads have been suspended.
        /// </summary>
        public bool IsReadSuspended { get; private set; }

        /// <summary>
        /// Provides direct access to the individual Media components of the input stream.
        /// </summary>
        public MediaComponentSet Components { get; private set; }

        /// <summary>
        /// Picture attachments are required when video streams support them
        /// and these attached packets must be read before reading the first frame
        /// of the stream and after seeking. This property is not part of the public API
        /// and is meant more for internal purposes
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

        #region Constructor and Initialization

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
            Utils.RegisterFFmpeg();

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
                    InputContext = ffmpeg.avformat_alloc_context();

                    // Try to open the input
                    fixed (AVFormatContext** inputContext = &InputContext)
                    {
                        // Open the input file
                        var openResult = ffmpeg.avformat_open_input(inputContext, MediaUrl, inputFormat, formatOptions.Reference);

                        // Validate the open operation
                        if (openResult < 0) throw new MediaContainerException($"Could not open '{MediaUrl}'. Error code: {openResult}");
                    }

                    // Set some general properties
                    MediaFormatName = Utils.PtrToString(InputContext->iformat->name);

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
                IsRealtimeStream = new[] { "rtp", "rtsp", "sdp" }.Any(s => MediaFormatName.Equals(s)) ||
                    (InputContext->pb != null && new[] { "rtp:", "udp:" }.Any(s => MediaUrl.StartsWith(s)));

                RequiresReadDelay = MediaFormatName.Equals("rstp") || MediaUrl.StartsWith("mmsh:");
                var inputAllowsDiscontinuities = (InputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;
                MediaSeeksByBytes = inputAllowsDiscontinuities && (MediaFormatName.Equals("ogg") == false);

                // Compute timespans
                MediaStartTime = InputContext->start_time.ToTimeSpan();
                MediaDuration = InputContext->duration.ToTimeSpan();

                if (MediaStartTime != TimeSpan.MinValue && MediaDuration != TimeSpan.MinValue)
                    MediaEndTime = MediaStartTime + MediaDuration;
                else
                    MediaEndTime = TimeSpan.MinValue;

                // Open the best suitable streams. Throw if no audio and/or video streams are found
                Components = CreateStreamComponents();

                // For network streams, figure out if reads can be paused and then start them.
                CanReadSuspend = ffmpeg.av_read_pause(InputContext) == 0;
                ffmpeg.av_read_play(InputContext);
                IsReadSuspended = false;

                // Initially and depending on the video component, rquire picture attachments.
                // Picture attachments are only required after the first read or after a seek.
                RequiresPictureAttachments = true;

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

        #endregion

        /// <summary>
        /// Reads the next available packet, sending the packet to the corresponding
        /// internal media component. It also sets IsAtEndOfStream property.
        /// Return true if the packet was accepted by any of the media components.
        /// Returns false if the packet was not accepted by any of the media components
        /// or if reading failed (i.e. End of stream or read error)
        /// </summary>
        /// <exception cref="MediaContainerException"></exception>
        public bool StreamReadNextPacket()
        {
            // Ensure read is not suspended
            StreamReadResume();

            if (RequiresReadDelay)
            {
                // in ffplay.c this is referenced via CONFIG_RTSP_DEMUXER || CONFIG_MMSH_PROTOCOL
                var millisecondsDifference = (int)Math.Round(DateTime.UtcNow.Subtract(LastReadTimeUtc).TotalMilliseconds, 2);
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
                    Components.Video.SendEmptyPacket();
                }

                RequiresPictureAttachments = false;
            }

            // Allocate the packet to read
            var readPacket = ffmpeg.av_packet_alloc();
            var readResult = ffmpeg.av_read_frame(InputContext, readPacket);
            LastReadTimeUtc = DateTime.UtcNow;

            if (readResult < 0)
            {
                // Handle failed packet reads. We don't need the allocated packet anymore
                ffmpeg.av_packet_free(&readPacket);

                // Detect an end of file situation (makes the readers enter draining mode)
                if ((readResult == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(InputContext->pb) != 0))
                {
                    // Force the decoders to enter draining mode (with empry packets)
                    if (IsAtEndOfStream == false)
                        Components.SendEmptyPackets();

                    IsAtEndOfStream = true;
                    return false;
                }
                else
                {
                    if (InputContext->pb != null && InputContext->pb->error != 0)
                        throw new MediaContainerException($"Input has produced an error. Error Code {readResult}, {ffmpeg.ErrorMessage(readResult)}");
                }
            }
            else
            {
                IsAtEndOfStream = false;
            }

            // Check if we were able to feed the packet. If not, simply discard it
            if (readPacket != null)
            {
                var wasPacketAccepted = Components.SendPacket(readPacket);
                if (wasPacketAccepted == false)
                    ffmpeg.av_packet_free(&readPacket);

                return wasPacketAccepted;
            }

            return false;
        }

        private void StreamReadSuspend()
        {
            if (InputContext == null || CanReadSuspend == false || IsReadSuspended) return;
            ffmpeg.av_read_pause(InputContext);
            IsReadSuspended = true;
        }

        private void StreamReadResume()
        {
            if (InputContext == null || CanReadSuspend == false || IsReadSuspended == false) return;
            ffmpeg.av_read_play(InputContext);
            IsReadSuspended = false;
        }

        private void StreamSeek(TimeSpan targetTime)
        {
            // TODO: Seeking and resetting attached picture
            // This method is WIP and missing some stufff
            // like seeking by byes and others.

            if (IsStreamSeekable == false)
            {
                $"Unable to seek. Underlying stream does not support seeking.".Warn(typeof(MediaContainer));
                return;
            }

            if (MediaSeeksByBytes == false)
            {
                if (targetTime > MediaEndTime) targetTime = MediaEndTime;
                if (targetTime < MediaStartTime) targetTime = MediaStartTime;
            }

            var seekFlags = MediaSeeksByBytes ? ffmpeg.AVSEEK_FLAG_BYTE : 0;

            var streamIndex = -1;
            var timeBase = ffmpeg.AV_TIME_BASE_Q;
            var component = Components.HasVideo ?
                Components.Video as MediaComponent : Components.Audio as MediaComponent;

            if (component == null) return;

            // Check if we really need to seek.
            if (component.LastFrameTime == targetTime)
                return;

            streamIndex = component.StreamIndex;
            timeBase = component.Stream->time_base;
            var seekTarget = (long)Math.Round(targetTime.TotalSeconds * timeBase.den / timeBase.num, 0);

            var startTime = DateTime.Now;
            var seekResult = ffmpeg.avformat_seek_file(InputContext, streamIndex, long.MinValue, seekTarget, seekTarget, seekFlags);
            $"{DateTime.Now.Subtract(startTime).TotalMilliseconds,10:0.000} ms Long seek".Trace(typeof(MediaContainer));

            if (seekResult >= 0)
            {
                Components.ClearPacketQueues();
                RequiresPictureAttachments = true;
                IsAtEndOfStream = false;

                if (component != null)
                {
                    // Perform reads until the next component frame is obtained
                    // as we need to check where in the component stream we have landed after the seek.
                    $"SEEK INIT".Trace(typeof(MediaContainer));
                    var beforeFrameTime = component.LastFrameTime;
                    while (beforeFrameTime == component.LastFrameTime)
                        StreamReadNextPacket();

                    $"SEEK START | Current {component.LastFrameTime.TotalSeconds,10:0.000} | Target {targetTime.TotalSeconds,10:0.000}".Trace(typeof(MediaContainer));
                    while (component.LastFrameTime < targetTime)
                    {
                        StreamReadNextPacket();
                        if (IsAtEndOfStream)
                            break;
                    }

                    $"SEEK END   | Current {component.LastFrameTime.TotalSeconds,10:0.000} | Target {targetTime.TotalSeconds,10:0.000}".Trace(typeof(MediaContainer));
                }
            }
            else
            {

            }
        }

        #region IDisposable Support

        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposed)
            {
                if (alsoManaged)
                {
                    if (InputContext != null)
                    {
                        fixed (AVFormatContext** inputContext = &InputContext)
                            ffmpeg.avformat_close_input(inputContext);

                        ffmpeg.avformat_free_context(InputContext);

                        Components.Dispose();
                    }
                }

                IsDisposed = true;
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
