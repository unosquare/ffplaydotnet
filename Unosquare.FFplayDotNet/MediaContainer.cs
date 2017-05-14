namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.FFplayDotNet.Decoding;
    using Unosquare.Swan;

    /// <summary>
    /// A container capable of opening an input url,
    /// reading packets from it, decoding frames, seeking, and pausing and resuming network streams
    /// Code heavily based on https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c
    /// The method pipeline should be: 
    /// 1. Set Options (or don't for automatic options) and Initialize, 
    /// 2. Perform continuous Reads, 
    /// 3. Perform continuous Decode and Materialize
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public unsafe sealed class MediaContainer : IDisposable
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
        /// Determines if the stream seeks by bytes always
        /// </summary>
        private bool MediaSeeksByBytes = false;

        /// <summary>
        /// Hold the value for the internal property with the same name.
        /// Picture attachments are required when video streams support them
        /// and these attached packets must be read before reading the first frame
        /// of the stream and after seeking.
        /// </summary>
        private bool m_RequiresPictureAttachments = true;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the media URL. This is the input url, file or device that is read
        /// by this container.
        /// </summary>
        public string MediaUrl { get; private set; }

        /// <summary>
        /// The media initialization options.
        /// Options are applied when calling the Initialize method.
        /// After nitialization, changing the options has no effect.
        /// </summary>
        public MediaOptions MediaOptions { get; } = new MediaOptions();

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
        /// Gets a value indicating whether an Input Context has been initialize.
        /// </summary>
        public bool IsInitialized { get { return InputContext != null; } }

        /// <summary>
        /// If available, the title will be extracted from the metadata of the media.
        /// Otherwise, this will be set to false.
        /// </summary>
        public string MediaTitle { get; private set; }

        /// <summary>
        /// Gets the media start time. It could be something other than 0.
        /// If this start time is not available (i.e. realtime media) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaRelativeStartTime { get; private set; }

        /// <summary>
        /// Gets the absolute start time of the media.
        /// Returns 0 when this information is available. Returns
        /// TimeSapn.MinValue wehn this information is not available.
        /// </summary>
        public TimeSpan MediaStartTime { get; private set; }

        /// <summary>
        /// Gets the duration of the media.
        /// If this information is not available (i.e. realtime media) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaDuration { get; private set; }

        /// <summary>
        /// Gets the end time of the media including any offsets of the input URL.
        /// If this information is not available (i.e. realtime streams) it will
        /// be set to TimeSpan.MinValue
        /// </summary>
        public TimeSpan MediaRelativeEndTime { get; private set; }

        /// <summary>
        /// Gets the absolute end time of the media by substracting the relative start time.
        /// TimeSapn.MinValue when this information is not available.
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
                if (InputContext == null || InputContext->pb == null) return 0;
                return InputContext->pb->pos;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the underlying media is seekable.
        /// </summary>
        public bool IsStreamSeekable { get { return MediaDuration.TotalSeconds > 0 && MediaDuration != TimeSpan.MinValue; } }

        /// <summary>
        /// Gets a value indicating whether this container represents realtime media.
        /// If the format name is rtp, rtsp, or sdp or if the url starts with udp: or rtp:
        /// then this property will be set to true.
        /// </summary>
        public bool IsStreamRealtime { get; private set; }

        /// <summary>
        /// Provides direct access to the individual Media components of the input stream.
        /// </summary>
        public MediaComponentSet Components { get; } = new MediaComponentSet();

        #endregion

        #region Internal Properties

        /// <summary>
        /// Gets the time the last packet was read from the input
        /// </summary>
        internal DateTime StreamLastReadTimeUtc { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// For RTSP and other realtime streams reads can be suspended.
        /// </summary>
        internal bool CanReadSuspend { get; private set; }

        /// <summary>
        /// For RTSP and other realtime streams reads can be suspended.
        /// This property will return true if reads have been suspended.
        /// </summary>
        internal bool IsReadSuspended { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a packet read delay witll be enforced.
        /// RSTP formats or MMSH Urls will have this property set to true.
        /// Reading packets will block for at most 10 milliseconds depending on the last read time.
        /// This is a hack according to the source code in ffplay.c
        /// </summary>
        internal bool RequiresReadDelay { get; private set; }

        /// <summary>
        /// Picture attachments are required when video streams support them
        /// and these attached packets must be read before reading the first frame
        /// of the stream and after seeking. This property is not part of the public API
        /// and is meant more for internal purposes
        internal bool RequiresPictureAttachments
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
        /// <exception cref="System.ArgumentNullException">mediaUrl</exception>
        public MediaContainer(string mediaUrl)
        {
            // Argument Validation
            if (string.IsNullOrWhiteSpace(mediaUrl))
                throw new ArgumentNullException($"{nameof(mediaUrl)}");

            // Initialize the library (if not already done)
            Utils.RegisterFFmpeg();

            // Create the options object
            MediaUrl = mediaUrl;
        }

        /// <summary>
        /// Initializes the input context to start read operations.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The input context has already been initialized.</exception>
        /// <exception cref="MediaContainerException"></exception>
        private void InitializeInputContext()
        {
            if (IsInitialized)
                throw new InvalidOperationException("The input context has already been initialized.");

            // Retrieve the input format (null = auto for default)
            AVInputFormat* inputFormat = null;
            if (string.IsNullOrWhiteSpace(MediaOptions.ForcedInputFormat) == false)
            {
                inputFormat = ffmpeg.av_find_input_format(MediaOptions.ForcedInputFormat);
                $"Format '{MediaOptions.ForcedInputFormat}' not found. Will use automatic format detection.".Warn(typeof(MediaContainer));
            }

            try
            {
                // Create the input format context, and open the input based on the provided format options.
                using (var formatOptions = new FFDictionary(MediaOptions.FormatOptions))
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
                if (MediaOptions.GeneratePts) InputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
                ffmpeg.av_format_inject_global_side_data(InputContext);

                // This is useful for file formats with no headers such as MPEG. This function also computes the real framerate in case of MPEG-2 repeat frame mode.
                if (ffmpeg.avformat_find_stream_info(InputContext, null) < 0)
                    $"{MediaUrl}: could read stream info.".Warn(typeof(MediaContainer));

                // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end
                if (InputContext->pb != null) InputContext->pb->eof_reached = 0;

                // Setup initial state variables
                MediaTitle = FFDictionary.GetEntry(InputContext->metadata, EntryName.Title, false)?.Value;
                IsStreamRealtime = new[] { "rtp", "rtsp", "sdp" }.Any(s => MediaFormatName.Equals(s)) ||
                    (InputContext->pb != null && new[] { "rtp:", "udp:" }.Any(s => MediaUrl.StartsWith(s)));

                RequiresReadDelay = MediaFormatName.Equals("rstp") || MediaUrl.StartsWith("mmsh:");
                var inputAllowsDiscontinuities = (InputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;
                MediaSeeksByBytes = inputAllowsDiscontinuities && (MediaFormatName.Equals("ogg") == false);

                // Compute timespans
                MediaRelativeStartTime = InputContext->start_time.ToTimeSpan();
                MediaDuration = InputContext->duration.ToTimeSpan();
                MediaEndTime = MediaDuration;

                if (MediaRelativeStartTime != TimeSpan.MinValue && MediaDuration != TimeSpan.MinValue)
                {
                    MediaStartTime = TimeSpan.Zero;
                    MediaRelativeEndTime = TimeSpan.FromTicks(MediaRelativeStartTime.Ticks + MediaDuration.Ticks);
                }
                else
                {
                    MediaStartTime = TimeSpan.MinValue;
                    MediaRelativeEndTime = TimeSpan.MinValue;
                }

                // Open the best suitable streams. Throw if no audio and/or video streams are found
                CreateStreamComponents();

                // For network streams, figure out if reads can be paused and then start them.
                CanReadSuspend = ffmpeg.av_read_pause(InputContext) == 0;
                ffmpeg.av_read_play(InputContext);
                IsReadSuspended = false;

                // Initially and depending on the video component, rquire picture attachments.
                // Picture attachments are only required after the first read or after a seek.
                RequiresPictureAttachments = true;

                // Seek to the begining of the file
                StreamSeekToStart();

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
        private void CreateStreamComponents()
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

                if (MediaOptions.IsVideoDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                        ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1,
                                            &requestedCodec, 0);

                if (MediaOptions.IsAudioDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        &requestedCodec, 0);

                if (MediaOptions.IsSubtitleDisabled == false)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                    ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                        (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                        &requestedCodec, 0);
            }

            var allMediaTypes = Enum.GetValues(typeof(MediaType));

            foreach (var mediaTypeItem in allMediaTypes)
            {
                if ((int)mediaTypeItem < 0) continue;
                var mediaType = (MediaType)mediaTypeItem;

                try
                {
                    if (streamIndexes[(int)mediaType] >= 0)
                    {
                        switch (mediaType)
                        {
                            case MediaType.Video:
                                Components[mediaType] = new VideoComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            case MediaType.Audio:
                                Components[mediaType] = new AudioComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            case MediaType.Subtitle:
                                Components[mediaType] = new SubtitleComponent(this, streamIndexes[(int)mediaType]);
                                break;
                            default:
                                continue;
                        }

                        if (Debugger.IsAttached)
                            $"{mediaType}: Selected Stream Index = {Components[mediaType].StreamIndex}".Info(typeof(MediaContainer));
                    }

                }
                catch (Exception ex)
                {
                    $"Unable to initialize {mediaType.ToString()} component. {ex.Message}".Error(typeof(MediaContainer));
                }
            }

            // Verify we have at least 1 valid stream component to work with.
            if (Components.HasVideo == false && Components.HasAudio == false)
                throw new MediaContainerException($"{MediaUrl}: No audio or video streams found to decode.");

        }

        #endregion

        #region Public API

        /// <summary>
        /// Initializes the input context in order to start reading from the Media URL.
        /// Any Media Options must be set before this method is called.
        /// </summary>
        public void Initialize()
        {
            InitializeInputContext();
        }

        /// <summary>
        /// Reads the next available packet, sending the packet to the corresponding
        /// internal media component. It also sets IsAtEndOfStream property.
        /// Returns true if the packet was accepted by any of the media components.
        /// Returns false if the packet was not accepted by any of the media components
        /// or if reading failed (i.e. End of stream or read error)
        /// </summary>
        /// <exception cref="MediaContainerException"></exception>
        public MediaType ReadNext()
        {
            // Check the context has been initialized
            if (IsInitialized == false)
                throw new InvalidOperationException($"Please call the {nameof(Initialize)} method before attempting this operation.");

            // Ensure read is not suspended
            StreamReadResume();

            if (RequiresReadDelay)
            {
                // in ffplay.c this is referenced via CONFIG_RTSP_DEMUXER || CONFIG_MMSH_PROTOCOL
                var millisecondsDifference = (int)Math.Round(DateTime.UtcNow.Subtract(StreamLastReadTimeUtc).TotalMilliseconds, 2);
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
            StreamLastReadTimeUtc = DateTime.UtcNow;

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
                    return MediaType.None;
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
                var componentType = Components.SendPacket(readPacket);

                // Discard the packet -- it was not accepted by any component
                if (componentType == MediaType.None)
                    ffmpeg.av_packet_free(&readPacket);

                return componentType;
            }

            return MediaType.None;
        }

        /// <summary>
        /// Decodes the next available packet in the packet queue for each of the components.
        /// Returns the list of decoded frames.
        /// A Packet may contain 0 or more frames.
        /// </summary>
        /// <param name="sortFrames">if set to <c>true</c> the resulting list of frames will be sorted by StartTime ascending</param>
        /// <returns></returns>
        public List<FrameSource> DecodeNext(bool sortFrames = false)
        {
            var result = new List<FrameSource>(64);
            foreach (var component in Components.All)
                result.AddRange(component.DecodeNextPacket());

            if (sortFrames)
                result.Sort();

            return result;
        }

        /// <summary>
        /// Decodes the next available packet for the given frame source type.
        /// </summary>
        /// <typeparam name="T">Video Audio or Subtitle Frame Source</typeparam>
        /// <returns></returns>
        public List<T> DecodeNext<T>(bool sortFrames = false)
            where T : FrameSource
        {
            List<T> result = null;

            if (Components.HasVideo && typeof(T) == typeof(VideoFrameSource))
                result = Components.Video.DecodeNextPacket().Cast<T>().ToList();

            else if (Components.HasAudio && typeof(T) == typeof(AudioFrameSource))
                return Components.Audio.DecodeNextPacket().Cast<T>().ToList();

            else if (Components.HasSubtitles && typeof(T) == typeof(SubtitleFrameSource))
                return Components.Subtitles.DecodeNextPacket().Cast<T>().ToList();

            if (result == null) return new List<T>(0);
            if (sortFrames) result.Sort();

            return result;

        }

        /// <summary>
        /// Performs audio, video and subtitle conversions on the decoded input frame so data
        /// can be used as a Frame. Pleasde note that if the output is passed as a reference. 
        /// This works as follows: if the output reference is null it will be automatically instantiated 
        /// and returned by this function. This enables to  either intantiate or reuse a Frame. 
        /// This is important because buffer allocations are exepnsive operations and this allows you 
        /// to perform the allocation once and continue reusing thae same buffer.
        /// </summary>
        /// <param name="input">The raw frame source.</param>
        /// <param name="output">The target frame.</param>
        /// <param name="releaseInput">if set to <c>true</c> releases the raw frame source from unmanaged memory.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">input</exception>
        /// <exception cref="System.ArgumentException">
        /// input
        /// or
        /// input
        /// </exception>
        /// <exception cref="MediaContainerException">MediaType</exception>
        public Frame MaterializeFrame(FrameSource input, ref Frame output, bool releaseInput = true)
        {
            // Check the input parameters
            if (input == null)
                throw new ArgumentNullException($"{nameof(input)} cannot be null.");

            try
            {
                switch (input.MediaType)
                {
                    case MediaType.Video:
                        if (input.IsStale) throw new ArgumentException(
                            $"The {nameof(input)} {nameof(FrameSource)} has already been released (it's stale).");

                        if (Components.HasVideo) Components.Video.MaterializeFrame(input, ref output);
                        return output;

                    case MediaType.Audio:
                        if (input.IsStale) throw new ArgumentException(
                            $"The {nameof(input)} {nameof(FrameSource)} has already been released (it's stale).");

                        if (Components.HasAudio) Components.Audio.MaterializeFrame(input, ref output);
                        return output;

                    case MediaType.Subtitle:
                        // We don't need to heck if subtitles are stale because they are immediately released
                        // upon decoding. This is because there is no unmanaged allocator for AVSubtitle.

                        if (Components.HasSubtitles) Components.Subtitles.MaterializeFrame(input, ref output);
                        return output;

                    default:
                        throw new MediaContainerException($"Unable to materialize {nameof(MediaType)} {(int)input.MediaType}");
                }
            }
            finally
            {
                if (releaseInput)
                    input.Dispose();
            }

        }

        /// <summary>
        /// A specialized version of the Materialize Frame function. For additional info, look at the
        /// general version of this method.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="input">The raw frame source.</param>
        /// <param name="output">The target frame.</param>
        /// <param name="releaseInput">if set to <c>true</c> releases the raw frame source from unmanaged memory.</param>
        /// <returns></returns>
        public T MaterializeFrame<T>(FrameSource input, ref T output, bool releaseInput = true)
            where T : Frame
        {
            var outputFrame = output as Frame;
            return MaterializeFrame(input, ref outputFrame, releaseInput) as T;
        }

        #endregion

        #region Private Stream Methods

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

        /// <summary>
        /// Drops the seek frames that are no longer needed.
        /// Target time should be provided in absolute, 0-based time
        /// </summary>
        /// <param name="frames">The frames.</param>
        /// <param name="targetTime">The target time.</param>
        /// <returns></returns>
        private int DropSeekFrames(List<FrameSource> frames, TimeSpan targetTime)
        {
            var result = 0;
            if (frames.Count < 2) return result;
            frames.Sort();

            var framesToDrop = new List<int>(frames.Count);
            var frameType = frames[0].MediaType;

            for (var i = 0; i < frames.Count - 1; i++)
            {
                var currentFrame = frames[i];
                var nextFrame = frames[i + 1];

                if (currentFrame.StartTime >= targetTime)
                    break;
                else if (currentFrame.StartTime < targetTime && nextFrame.StartTime <= targetTime)
                    framesToDrop.Add(i);
            }

            for (var i = framesToDrop.Count - 1; i >= 0; i--)
            {
                var dropIndex = framesToDrop[i];
                var frame = frames[dropIndex];
                frames.RemoveAt(dropIndex);
                frame.Dispose();
                result++;
            }

            if (result > 0)
                $"Dropped {result,5} decoded {frameType,10} frames in seek operation.".Trace(typeof(MediaContainer));

            return result;
        }



        public void StreamSeekToStart()
        {
            if (MediaRelativeStartTime != TimeSpan.MinValue) return;
            var startSeekTime = (long)(MediaRelativeStartTime.TotalSeconds * ffmpeg.AV_TIME_BASE);
            var seekResult = ffmpeg.avformat_seek_file(InputContext, -1,
                long.MinValue, startSeekTime, long.MaxValue, 0);

            Components.ClearPacketQueues();
            RequiresPictureAttachments = true;
            IsAtEndOfStream = false;
        }

        public List<FrameSource> StreamSeek(TimeSpan targetTime, bool doPreciseSeek)
        {
            // TODO: Seeking and resetting attached picture
            // This method is WIP and missing some stufff
            // like seeking by byes and others.

            #region Setup

            var result = new List<FrameSource>();

            if (IsStreamSeekable == false)
            {
                $"Unable to seek. Underlying stream does not support seeking.".Warn(typeof(MediaContainer));
                return result;
            }

            // Select the main component
            var mainComp = Components.Main;
            if (mainComp == null) return result;

            var videoComponent = mainComp as VideoComponent;
            if (videoComponent != null)
                targetTime = TimeSpan.FromSeconds(targetTime.TotalSeconds.ToMultipleOf(1d / videoComponent.CurrentFrameRate));

            // clamp the target time to the component's bounds
            if (MediaSeeksByBytes == false)
            {
                if (targetTime > mainComp.EndTime) targetTime = mainComp.EndTime;
                if (targetTime < mainComp.StartTime) targetTime = mainComp.StartTime;
            }

            // Stream seeking by main component
            // The backward flag means that we want to seek to at MOST the target position
            var seekFlags = ffmpeg.AVSEEK_FLAG_BACKWARD | (MediaSeeksByBytes ? ffmpeg.AVSEEK_FLAG_BYTE : 0);
            var streamIndex = mainComp.StreamIndex;
            var timeBase = mainComp.Stream->time_base;

            // The seek target is computed by using the absolute, 0-based target time and adding the component stream's start time
            var relativeTargetTime = TimeSpan.FromTicks(targetTime.Ticks + mainComp.RelativeStartTime.Ticks);

            var seekTarget = (long)Math.Round(relativeTargetTime.TotalSeconds * timeBase.den / timeBase.num, 0) - 2;

            // Perform the stream seek
            var seekResult = 0;
            var startPos = StreamPosition;

            #endregion

            #region Perform Long Seek

            var startTime = DateTime.UtcNow;
            seekResult = ffmpeg.avformat_seek_file(InputContext, streamIndex, long.MinValue, seekTarget, seekTarget, seekFlags);
            //seekResult = ffmpeg.av_seek_frame(InputContext, streamIndex, seekTarget, seekFlags);
            $"SEEK L: Elapsed: {startTime.DebugElapsedUtc()} | Target: {targetTime.Debug()} | Seek: {seekTarget.Debug()} | P0: {startPos.Debug(1024)} | P1: {StreamPosition.Debug(1024)} ".Trace(typeof(MediaContainer));


            // Flush the buffered packets and codec
            Components.ClearPacketQueues();
            RequiresPictureAttachments = true;
            IsAtEndOfStream = false;

            // Ensure we had a successful seek operation
            if (seekResult < 0)
            {
                $"Seek operation failed. Error code {seekResult}, {ffmpeg.ErrorMessage(seekResult)}".Warn(typeof(MediaContainer));
                return result;
            }

            #endregion

            #region Precise Seek

            // If precise seek not requested simply return.
            if (doPreciseSeek == false) return result;

            // Perform frame based seek. Packet seek is not good enough
            var outputPacketStats = true;
            var shortSeekCycles = 0;
            startPos = StreamPosition;

            // Create a holder of frame lists; one for each type of media
            var outputFrames = new Dictionary<MediaType, List<FrameSource>>();
            foreach (var c in Components.All)
                outputFrames[c.MediaType] = new List<FrameSource>();

            // Start reading and decoding util we reach the target
            while (IsAtEndOfStream == false && doPreciseSeek)
            {
                shortSeekCycles++;
                var mediaType = ReadNext();
                if (outputFrames.ContainsKey(mediaType) == false)
                    continue;

                // Output statistics on the frame that was first decoded.
                outputFrames[mediaType].AddRange(Components[mediaType].DecodeNextPacket());
                if (outputPacketStats && outputFrames[mediaType].Count > 0)
                {
                    var firstFrame = outputFrames[mediaType][0];
                    $"SEEK P: Elapsed: {startTime.DebugElapsedUtc()} | PKT: {firstFrame.PacketStartTime.Debug()} | FR: {firstFrame.StartTime.Debug()} | P0: {startPos.Debug(1024)} | P1: {StreamPosition.Debug(1024)} ".Trace(typeof(MediaContainer));
                    outputPacketStats = false;
                }

                // check if we are done with precise seeking
                // all streams must have at least 1 frame in the range
                var isDoneSeeking = true;
                foreach (var kvp in outputFrames)
                {
                    var componentFrames = kvp.Value;
                    if (componentFrames.Count >= 24)
                        DropSeekFrames(componentFrames, targetTime);

                    var hasTargetRange = componentFrames.Count > 0
                        && componentFrames.Max(f => f.StartTime) >= targetTime;

                    if (hasTargetRange == false)
                    {
                        isDoneSeeking = false;
                        break;
                    }
                }

                if (isDoneSeeking)
                    break;
            }

            // Perform one final cleanup and aggregate the frames into a single
            // interleaved collection
            foreach (var kvp in outputFrames)
            {
                var componentFrames = kvp.Value;
                DropSeekFrames(componentFrames, targetTime);
                result.AddRange(componentFrames);
            }

            result.Sort();

            $"SEEK F: Elapsed: {startTime.DebugElapsedUtc()} | CY: {shortSeekCycles} | FR: {result.Count} | P0: {startPos.Debug(1024)} | P1: {StreamPosition.Debug(1024)} ".Trace(typeof(MediaContainer));

            #endregion

            return result;

        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged">
        ///   <c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool alsoManaged)
        {
            if (IsDisposed) return;

            if (alsoManaged)
            {
                Components.Dispose();

                if (InputContext != null)
                {
                    StreamReadSuspend();
                    fixed (AVFormatContext** inputContext = &InputContext)
                        ffmpeg.avformat_close_input(inputContext);

                    ffmpeg.avformat_free_context(InputContext);

                    InputContext = null;
                }
            }

            IsDisposed = true;
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}
