namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.FFplayDotNet.Primitives;
    using static Unosquare.FFplayDotNet.SDL;

    // TODO: remove goto s

    public unsafe class MediaState
    {
        //internal readonly GCHandle Handle; // TODO: ensure free is called
        internal readonly FFplay Player;

        internal AVInputFormat* InputFormat;
        internal AVFormatContext* InputContext;

        internal AVStream* AudioStream;
        internal AVStream* SubtitleStream;
        internal AVStream* VideoStream;

        internal SwrContext* AudioScaler;
        internal SwsContext* VideoScaler;

        internal bool WasPaused;
        internal int ReadPauseResult;

        internal bool EnqueuePacketAttachments;

        internal int LastVideoStreamIndex;
        internal int LastAudioStreamIndex;
        internal int LastSubtitleStreamIndex;

        internal byte* RenderAudioBuffer;
        internal int RenderAudioBufferIndex; /* in bytes */
        internal uint RenderAudioBufferLength; /* in bytes */
        internal byte* ResampledAudioBuffer = null;
        internal uint ResampledAudioBufferLength;

        internal double DecodedAudioClockPosition;
        internal int DecodedAudioClockSerial;

        internal double AudioSkewCummulative; /* used for AV difference average computation */
        internal double AudioSkewCoefficient;
        internal double AudioSkewThreshold;
        internal int AudioSkewAvgCount;

        /// <summary>
        /// Gets displayed video frame time in seconds.
        /// Port of frame_timer
        /// </summary>
        public double VideoFrameTimeSeconds { get; internal set; }


        internal readonly LockCondition IsFrameDecoded;


        public bool? IsPtsReorderingEnabled { get; internal set; } = null;

        public bool IsAbortRequested { get; internal set; }

        /// <summary>
        /// Port of force_refresh
        /// </summary>

        public bool IsVideoRefreshRequested { get; internal set; }

        public bool IsPaused { get; internal set; }

        public bool IsSeekRequested { get; internal set; }

        public int SeekModeFlags { get; internal set; }

        public long SeekTargetPosition { get; internal set; }

        public long SeekTargetRange { get; internal set; }

        public bool IsMediaRealtime
        {
            get
            {
                if (InputContext == null)
                    return false;

                var formatName = Native.BytePtrToString(InputContext->iformat->name);
                var filename = Encoding.GetEncoding(0).GetString(InputContext->filename);

                if (formatName.Equals("rtp")
                   || formatName.Equals("rtsp")
                   || formatName.Equals("sdp")
                )
                    return true;

                if (InputContext->pb != null &&
                    (filename.StartsWith("rtp:") || filename.StartsWith("udp:")))
                    return true;

                return false;

            }
        }

        internal Clock AudioClock { get; set; }
        internal Clock VideoClock { get; set; }
        internal Clock ExternalClock { get; set; }

        public FrameQueue VideoQueue { get; internal set; }
        public FrameQueue SubtitleQueue { get; internal set; }
        public FrameQueue AudioQueue { get; internal set; }

        public Decoder AudioDecoder { get; internal set; }
        public Decoder VideoDecoder { get; internal set; }
        public Decoder SubtitleDecoder { get; internal set; }

        public int AudioStreamIndex { get; set; }
        public int VideoStreamIndex { get; internal set; }
        public int SubtitleStreamIndex { get; internal set; }

        public SyncMode MediaSyncMode { get; set; }

        /// <summary>
        /// Gets the master synchronize mode.
        /// Port of get_master_sync_type
        /// </summary>
        public SyncMode MasterSyncMode
        {
            get
            {
                if (MediaSyncMode == SyncMode.Video)
                {
                    if (VideoStream != null)
                        return SyncMode.Video;
                    else
                        return SyncMode.Audio;
                }
                else if (MediaSyncMode == SyncMode.Audio)
                {
                    if (AudioStream != null)
                        return SyncMode.Audio;
                    else
                        return SyncMode.External;
                }
                else
                {
                    return SyncMode.External;
                }
            }
        }

        /// <summary>
        /// Gets the master clock position seconds.
        /// Port of get_master_clock
        public double MasterClockPositionSeconds
        {
            get
            {
                switch (MasterSyncMode)
                {
                    case SyncMode.Video:
                        return VideoClock.PositionSeconds;

                    case SyncMode.Audio:
                        return AudioClock.PositionSeconds;

                    default:
                        return ExternalClock.PositionSeconds;

                }
            }
        }

        internal PacketQueue VideoPackets { get; } = new PacketQueue();
        internal PacketQueue AudioPackets { get; } = new PacketQueue();
        internal PacketQueue SubtitlePackets { get; } = new PacketQueue();

        public AudioParams AudioInputParams { get; } = new AudioParams();
        public AudioParams AudioOutputParams { get; } = new AudioParams();

        public int AudioVolume { get; set; }
        public bool IsAudioMuted { get; set; }
        public int AudioHardwareBufferSize { get; internal set; }

        /// <summary>
        /// Gets the maximum duration of the frame in seconds.
        /// above this, we consider the jump a timestamp discontinuity
        /// </summary>
        public double MaximumFrameDuration { get; internal set; }

        /// <summary>
        /// The amount of video frames that have been dropped early.
        /// Port of frame_drops_early
        /// </summary>
        public int VideoFrameEarlyDrops { get; internal set; }

        /// <summary>
        /// The amount of video frames that have been dropped early.
        /// Port of frame_drops_late
        /// </summary>
        public int VideoFrameLateDrops { get; internal set; }

        public bool IsAtEndOfFile { get; internal set; }
        public string MediaUrl { get; internal set; }
        public bool IsFrameStepping { get; internal set; }

        #region Constructors

        internal MediaState(FFplay player)
        {
            //Handle = GCHandle.Alloc(this, GCHandleType.Pinned);

            Player = player;
            MediaUrl = player.Options.MediaInputUrl;
            InputFormat = player.InputForcedFormat;

            VideoQueue = new FrameQueue(VideoPackets, Constants.VideoQueueSize, true);
            SubtitleQueue = new FrameQueue(SubtitlePackets, Constants.SubtitleQueueSize, false);
            AudioQueue = new FrameQueue(AudioPackets, Constants.SampleQueueSize, true);

            IsFrameDecoded = new LockCondition();

            VideoClock = new Clock(() => { return new int?(VideoPackets.Serial); });
            AudioClock = new Clock(() => { return new int?(AudioPackets.Serial); });
            ExternalClock = new Clock(null);

            DecodedAudioClockSerial = -1;
            AudioVolume = SDL_MIX_MAXVOLUME;
            IsAudioMuted = false;
            MediaSyncMode = player.Options.MediaSyncMode;

        }

        #endregion

        #region Opening and Closing Streams

        /// <summary>
        /// Opens a stream component based on the specified stream index
        /// Port of stream_component_open
        /// </summary>
        /// <param name="streamIndex">The stream index component</param>
        /// <returns></returns>
        internal int OpenStreamComponent(int streamIndex)
        {
            var input = InputContext;
            string forcedCodecName = null;
            FFDictionaryEntry kvp = null;

            var sampleRate = 0;
            var channelCount = 0;
            var channelLayout = 0L;
            var result = 0;

            int lowResIndex = Convert.ToInt32(Player.Options.EnableLowRes);
            if (streamIndex < 0 || streamIndex >= input->nb_streams)
                return -1;

            var codecContext = ffmpeg.avcodec_alloc_context3(null);
            if (codecContext == null)
                return ffmpeg.AVERROR_ENOMEM;

            result = ffmpeg.avcodec_parameters_to_context(codecContext, input->streams[streamIndex]->codecpar);
            if (result < 0)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                return result;
            }

            ffmpeg.av_codec_set_pkt_timebase(codecContext, input->streams[streamIndex]->time_base);
            var decoder = ffmpeg.avcodec_find_decoder(codecContext->codec_id);

            switch (codecContext->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO: LastAudioStreamIndex = streamIndex; forcedCodecName = Player.AudioCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE: LastSubtitleStreamIndex = streamIndex; forcedCodecName = Player.SubtitleCodecName; break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO: LastVideoStreamIndex = streamIndex; forcedCodecName = Player.VideoCodecName; break;
            }

            if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
                decoder = ffmpeg.avcodec_find_decoder_by_name(forcedCodecName);

            if (decoder == null)
            {
                if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with name '{forcedCodecName}'\n");
                else
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with id {codecContext->codec_id}\n");

                result = ffmpeg.AVERROR_EINVAL;
                ffmpeg.avcodec_free_context(&codecContext);
                return result;
            }

            codecContext->codec_id = decoder->id;

            if (lowResIndex > ffmpeg.av_codec_get_max_lowres(decoder))
            {
                ffmpeg.av_log(codecContext, ffmpeg.AV_LOG_WARNING, $"The maximum value for lowres supported by the decoder is {ffmpeg.av_codec_get_max_lowres(decoder)}\n");
                lowResIndex = ffmpeg.av_codec_get_max_lowres(decoder);
            }

            ffmpeg.av_codec_set_lowres(codecContext, lowResIndex);

            if (lowResIndex != 0)
                codecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            if (Player.Options.EnableFastDecoding)
                codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if ((decoder->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                codecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            var codecOptions = Player.Options.CodecOptions.FilterOptions(codecContext->codec_id, input, input->streams[streamIndex], decoder);

            if (codecOptions.KeyExists("threads") == false)
                codecOptions["threads"] = "auto";

            if (lowResIndex != 0)
                codecOptions["lowres"] = lowResIndex.ToString();

            if (codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                codecOptions["refcounted_frames"] = "1";

            if ((result = ffmpeg.avcodec_open2(codecContext, decoder, codecOptions.Reference)) < 0)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                return result;
            }

            // On return of avcodec_open2, codecOptions will be filled with options that were not found.
            if ((kvp = codecOptions.First()) != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"Option {kvp.Key} not found.\n");
                // Do not fail just because of an invalid option.
                //result = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                //goto fail;
            }

            IsAtEndOfFile = false;
            input->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;

            switch (codecContext->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    if ((result = Player.audio_open(channelLayout, channelCount, sampleRate, AudioOutputParams)) < 0)
                    {
                        ffmpeg.avcodec_free_context(&codecContext);
                        return result;
                    }

                    AudioHardwareBufferSize = result;
                    AudioOutputParams.CopyTo(AudioInputParams);
                    RenderAudioBufferLength = 0;
                    RenderAudioBufferIndex = 0;
                    AudioSkewCoefficient = Math.Exp(Math.Log(0.01) / Constants.AudioSkewMinSamples);
                    AudioSkewAvgCount = 0;
                    AudioSkewThreshold = (double)(AudioHardwareBufferSize) / AudioOutputParams.BytesPerSecond;
                    AudioStreamIndex = streamIndex;
                    AudioStream = input->streams[streamIndex];

                    AudioDecoder = new Decoder(this, codecContext, AudioPackets, IsFrameDecoded);

                    if ((InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                        InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
                    {
                        AudioDecoder.StartPts = AudioStream->start_time;
                        AudioDecoder.StartPtsTimebase = AudioStream->time_base;
                    }

                    AudioDecoder.Start(FFplay.DecodeAudioQueueContinuously);
                    SDL_PauseAudio(0);
                    break;

                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoStreamIndex = streamIndex;
                    VideoStream = input->streams[streamIndex];
                    VideoDecoder = new Decoder(this, codecContext, VideoPackets, IsFrameDecoded);
                    result = VideoDecoder.Start(FFplay.DecodeVideoQueueContinuously);
                    EnqueuePacketAttachments = true;
                    break;

                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleStreamIndex = streamIndex;
                    SubtitleStream = input->streams[streamIndex];
                    SubtitleDecoder = new Decoder(this, codecContext, SubtitlePackets, IsFrameDecoded);
                    result = SubtitleDecoder.Start(FFplay.DecodeSubtitlesQueueContinuously);
                    break;

                default:
                    break;
            }

            return result;
        }

        /// <summary>
        /// Port of stream_component_close
        /// </summary>
        /// <param name="streamIndex"></param>
        internal void CloseStreamComponent(int streamIndex)
        {
            var input = InputContext;

            if (streamIndex < 0 || streamIndex >= input->nb_streams)
                return;

            var codecParams = input->streams[streamIndex]->codecpar;
            switch (codecParams->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    AudioDecoder.Abort(AudioQueue);
                    SDL_CloseAudio();
                    AudioDecoder.ReleaseUnmanaged();
                    fixed (SwrContext** audioScalerReference = &AudioScaler)
                        ffmpeg.swr_free(audioScalerReference);

                    ffmpeg.av_freep((void*)ResampledAudioBuffer);
                    ResampledAudioBufferLength = 0;
                    RenderAudioBuffer = null;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoDecoder.Abort(VideoQueue);
                    VideoDecoder.ReleaseUnmanaged();
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleDecoder.Abort(SubtitleQueue);
                    SubtitleDecoder.ReleaseUnmanaged();
                    break;
                default:
                    break;
            }

            input->streams[streamIndex]->discard = AVDiscard.AVDISCARD_ALL;

            switch (codecParams->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    AudioStream = null;
                    AudioStreamIndex = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoStream = null;
                    VideoStreamIndex = -1;
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleStream = null;
                    SubtitleStreamIndex = -1;
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Closes the media along with all of its stream components.
        /// Port of stream_close
        /// </summary>
        internal void CloseStream()
        {
            IsAbortRequested = true;
            Player.MediaReadTask.Wait();

            if (AudioStreamIndex >= 0)
                CloseStreamComponent(AudioStreamIndex);

            if (VideoStreamIndex >= 0)
                CloseStreamComponent(VideoStreamIndex);

            if (SubtitleStreamIndex >= 0)
                CloseStreamComponent(SubtitleStreamIndex);

            fixed (AVFormatContext** vstic = &InputContext)
                ffmpeg.avformat_close_input(vstic);


            VideoPackets.Clear();
            AudioPackets.Clear();
            SubtitlePackets.Clear();
            VideoQueue.Clear();
            AudioQueue.Clear();
            SubtitleQueue.Clear();
            IsFrameDecoded.Dispose();

            ffmpeg.sws_freeContext(VideoScaler);

        }

        #endregion

        #region Uncompressing of Frames into Raw data

        /// <summary>
        /// Fills the Bitmap property of a Video frame.
        /// Port of upload_texture
        /// </summary>
        /// <param name="videoFrame">The video frame.</param>
        /// <returns></returns>
        internal bool UncompressVideoFrame(FrameHolder videoFrame)
        {
            if (videoFrame == null || videoFrame.DecodedFrame == null)
                return false;

            var frame = videoFrame.DecodedFrame;

            if ((AVPixelFormat)frame->format == Constants.OutputPixelFormat)
            {
                // We don't need to do any colorspace transformation.
                videoFrame.FillBitmapDataFromDecodedFrame();
                return true;
            }

            fixed (SwsContext** scalerReference = &VideoScaler)
            {
                // Retrieve a suitable scaler or create it on the fly
                *scalerReference = ffmpeg.sws_getCachedContext(*scalerReference,
                        frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height,
                        Constants.OutputPixelFormat, Player.Options.VideoScalerFlags, null, null, null);

                // Check for scaler availability
                if (*scalerReference == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                    return false;
                }

                // Fill the buffer from scaler
                videoFrame.FillBitmapDataFromScaler(*scalerReference);
            }

            return true;
        }

        /// <summary>
        /// Decode one audio frame and return its uncompressed size.
        /// The processed audio frame is decoded, converted if required, and
        /// stored in RenderAudioBuffer, with size in bytes given by the return value.
        /// Port of audio_decode_frame
        /// </summary>
        /// <returns></returns>
        internal int UncompressAudioFrame()
        {
            int decodedDataSize;
            int resampledDataSize;
            long decodedFrameChannelLayout;

            int wantedSampleCount;
            FrameHolder audioFrame = null;

            if (IsPaused) return -1;

            do
            {
                if (Helper.IsWindows)
                {
                    while (AudioQueue.PendingCount == 0)
                    {
                        if ((ffmpeg.av_gettime_relative() - Player.RednerAudioCallbackTimestamp) > 1000000L * AudioHardwareBufferSize / AudioOutputParams.BytesPerSecond / 2)
                            return -1;

                        Thread.Sleep(1); //ffmpeg.av_usleep(1000);
                    }
                }

                audioFrame = AudioQueue.PeekReadableFrame();
                if (audioFrame == null) return -1;
                AudioQueue.QueueNextRead();
            } while (audioFrame.Serial != AudioPackets.Serial);

            decodedDataSize = ffmpeg.av_samples_get_buffer_size(
                null, ffmpeg.av_frame_get_channels(audioFrame.DecodedFrame),
                audioFrame.DecodedFrame->nb_samples,
                (AVSampleFormat)audioFrame.DecodedFrame->format, 1);

            decodedFrameChannelLayout =
                (audioFrame.DecodedFrame->channel_layout != 0 && ffmpeg.av_frame_get_channels(audioFrame.DecodedFrame) == ffmpeg.av_get_channel_layout_nb_channels(audioFrame.DecodedFrame->channel_layout)) ?
                    Convert.ToInt64(audioFrame.DecodedFrame->channel_layout) :
                    ffmpeg.av_get_default_channel_layout(ffmpeg.av_frame_get_channels(audioFrame.DecodedFrame));

            wantedSampleCount = SynchronizeAudio(audioFrame.DecodedFrame->nb_samples);

            if (audioFrame.DecodedFrame->format != (int)AudioInputParams.SampleFormat ||
                decodedFrameChannelLayout != AudioInputParams.ChannelLayout ||
                audioFrame.DecodedFrame->sample_rate != AudioInputParams.Frequency ||
                (wantedSampleCount != audioFrame.DecodedFrame->nb_samples && AudioScaler == null))
            {
                fixed (SwrContext** audioScalerReference = &AudioScaler)
                    ffmpeg.swr_free(audioScalerReference);

                AudioScaler = ffmpeg.swr_alloc_set_opts(
                    null,
                    AudioOutputParams.ChannelLayout, AudioOutputParams.SampleFormat, AudioOutputParams.Frequency,
                    decodedFrameChannelLayout, (AVSampleFormat)audioFrame.DecodedFrame->format, audioFrame.DecodedFrame->sample_rate,
                    0, null);

                if (AudioScaler == null || ffmpeg.swr_init(AudioScaler) < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"Cannot create sample rate converter for conversion of {audioFrame.DecodedFrame->sample_rate} Hz {ffmpeg.av_get_sample_fmt_name((AVSampleFormat)audioFrame.DecodedFrame->format)} " +
                           "{ffmpeg.av_frame_get_channels(af.frame)} channels to {audio_tgt.freq} Hz {ffmpeg.av_get_sample_fmt_name(audio_tgt.fmt)} {audio_tgt.channels} " +
                           "channels!\n");

                    fixed (SwrContext** audoScalerReference = &AudioScaler)
                        ffmpeg.swr_free(audoScalerReference);

                    return -1;
                }

                AudioInputParams.ChannelLayout = decodedFrameChannelLayout;
                AudioInputParams.ChannelCount = ffmpeg.av_frame_get_channels(audioFrame.DecodedFrame);
                AudioInputParams.Frequency = audioFrame.DecodedFrame->sample_rate;
                AudioInputParams.SampleFormat = (AVSampleFormat)audioFrame.DecodedFrame->format;
            }

            if (AudioScaler != null)
            {
                var inputBuffer = audioFrame.DecodedFrame->extended_data;
                var outputBuffer = ResampledAudioBuffer;
                var outputSampleCount = wantedSampleCount * AudioOutputParams.Frequency / audioFrame.DecodedFrame->sample_rate + 256;
                var outputBufferLength = ffmpeg.av_samples_get_buffer_size(null, AudioOutputParams.ChannelCount, outputSampleCount, AudioOutputParams.SampleFormat, 0);

                if (outputBufferLength < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size() failed\n");
                    return -1;
                }

                if (wantedSampleCount != audioFrame.DecodedFrame->nb_samples)
                {
                    if (ffmpeg.swr_set_compensation(AudioScaler, (wantedSampleCount - audioFrame.DecodedFrame->nb_samples) * AudioOutputParams.Frequency / audioFrame.DecodedFrame->sample_rate,
                                                wantedSampleCount * AudioOutputParams.Frequency / audioFrame.DecodedFrame->sample_rate) < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_set_compensation() failed\n");
                        return -1;
                    }
                }

                fixed (uint* bufferLengthPointer = &ResampledAudioBufferLength)
                    ffmpeg.av_fast_malloc((void*)ResampledAudioBuffer, bufferLengthPointer, (ulong)outputBufferLength);

                if (ResampledAudioBuffer == null)
                    return ffmpeg.AVERROR_ENOMEM;

                var len2 = ffmpeg.swr_convert(AudioScaler, &outputBuffer, outputSampleCount, inputBuffer, audioFrame.DecodedFrame->nb_samples);
                if (len2 < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "swr_convert() failed\n");
                    return -1;
                }

                if (len2 == outputSampleCount)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "audio buffer is probably too small\n");
                    if (ffmpeg.swr_init(AudioScaler) < 0)
                        fixed (SwrContext** audioScalerReference = &AudioScaler)
                            ffmpeg.swr_free(audioScalerReference);
                }

                RenderAudioBuffer = ResampledAudioBuffer;
                resampledDataSize = len2 * AudioOutputParams.ChannelCount * ffmpeg.av_get_bytes_per_sample(AudioOutputParams.SampleFormat);
            }
            else
            {
                RenderAudioBuffer = audioFrame.DecodedFrame->data[0];
                resampledDataSize = decodedDataSize;
            }

            /* update the audio clock with the pts */
            if (!double.IsNaN(audioFrame.PtsSeconds))
                DecodedAudioClockPosition = audioFrame.PtsSeconds + (double)audioFrame.DecodedFrame->nb_samples / audioFrame.DecodedFrame->sample_rate;
            else
                DecodedAudioClockPosition = double.NaN;

            DecodedAudioClockSerial = audioFrame.Serial;

            return resampledDataSize;
        }

        #endregion

        #region Timing and Clocks

        internal void AdjustExternalClockSpeedRatio()
        {
            if (VideoStreamIndex >= 0 && VideoPackets.Count <= Constants.ExternalClockMinFrames ||
                AudioStreamIndex >= 0 && AudioPackets.Count <= Constants.ExternalClockMinFrames)
            {
                ExternalClock.SpeedRatio = (Math.Max(Constants.ExternalClockSpeedMin, ExternalClock.SpeedRatio - Constants.ExternalClockSpeedStep));
            }
            else if ((VideoStreamIndex < 0 || VideoPackets.Count > Constants.ExternalClockMaxFrames) &&
                     (AudioStreamIndex < 0 || AudioPackets.Count > Constants.ExternalClockMaxFrames))
            {
                ExternalClock.SpeedRatio = (Math.Min(Constants.ExternalClockSpeedMax, ExternalClock.SpeedRatio + Constants.ExternalClockSpeedStep));
            }
            else
            {
                var speedRatio = ExternalClock.SpeedRatio;
                if (speedRatio != 1.0)
                    ExternalClock.SpeedRatio = (speedRatio + Constants.ExternalClockSpeedStep * (1.0 - speedRatio) / Math.Abs(1.0 - speedRatio));
            }
        }

        internal double ComputeVideoClockDelay(double delaySeconds)
        {
            var clockSkewSeconds = 0d;

            if (MasterSyncMode != SyncMode.Video)
            {
                clockSkewSeconds = VideoClock.PositionSeconds - MasterClockPositionSeconds;
                var syncThreshold = Math.Max(
                    Constants.AvSyncThresholdMinSecs,
                    Math.Min(Constants.AvSyncThresholdMaxSecs, delaySeconds));

                if (!double.IsNaN(clockSkewSeconds) && Math.Abs(clockSkewSeconds) < MaximumFrameDuration)
                {
                    if (clockSkewSeconds <= -syncThreshold)
                        delaySeconds = Math.Max(0, delaySeconds + clockSkewSeconds);
                    else if (clockSkewSeconds >= syncThreshold && delaySeconds > Constants.AvSuncFrameDupThresholdSecs)
                        delaySeconds = delaySeconds + clockSkewSeconds;
                    else if (clockSkewSeconds >= syncThreshold)
                        delaySeconds = 2 * delaySeconds;
                }
            }

            ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"video: delay={delaySeconds} A-V={-clockSkewSeconds}\n");
            return delaySeconds;
        }

        internal double ComputeVideoFrameDurationSeconds(FrameHolder videoFrame, FrameHolder nextVideoFrame)
        {
            if (videoFrame.Serial == nextVideoFrame.Serial)
            {
                var durationSeconds = nextVideoFrame.PtsSeconds - videoFrame.PtsSeconds;
                if (double.IsNaN(durationSeconds) || durationSeconds <= 0 || durationSeconds > MaximumFrameDuration)
                    return videoFrame.DurationSeconds;
                else
                    return durationSeconds;
            }

            return 0.0;
        }

        internal void UpdateVideoPts(double pts, long pos, int serial)
        {
            VideoClock.SetPosition(pts, serial);
            ExternalClock.SyncTo(VideoClock);
        }

        /// <summary>
        /// When audio does not hold the master clock for synchronization,
        /// this method will try to remove or add samples to correct the clock.
        /// It gets the number of decoded audio samples and return the estimated
        /// wanted -- or ideal -- samples.
        /// </summary>
        /// <param name="audioSampleCount">The decoded audio sample count.</param>
        /// <returns>Returns the amount of samples required</returns>
        internal int SynchronizeAudio(int audioSampleCount)
        {
            var wantedAudioSampleCount = audioSampleCount;

            /* if not master, then */
            if (MasterSyncMode != SyncMode.Audio)
            {
                var audioSkew = AudioClock.PositionSeconds - MasterClockPositionSeconds;

                if (!double.IsNaN(audioSkew) && Math.Abs(audioSkew) < Constants.AvNoSyncThresholdSecs)
                {
                    AudioSkewCummulative = audioSkew + AudioSkewCoefficient * AudioSkewCummulative;
                    if (AudioSkewAvgCount < Constants.AudioSkewMinSamples)
                    {
                        /* not enough measures to have a correct estimate */
                        AudioSkewAvgCount++;
                    }
                    else
                    {
                        /* estimate the A-V difference */
                        var audioSkewAvg = AudioSkewCummulative * (1.0 - AudioSkewCoefficient);

                        if (Math.Abs(audioSkewAvg) >= AudioSkewThreshold)
                        {
                            wantedAudioSampleCount = audioSampleCount + (int)(audioSkew * AudioInputParams.Frequency);
                            var sampleCountMin = ((audioSampleCount * (100 - Constants.SampleCorrectionPercentMax) / 100));
                            var sampleCountMax = ((audioSampleCount * (100 + Constants.SampleCorrectionPercentMax) / 100));
                            wantedAudioSampleCount = ffmpeg.av_clip(wantedAudioSampleCount, sampleCountMin, sampleCountMax);
                        }

                        ffmpeg.av_log(null,
                            ffmpeg.AV_LOG_TRACE, $"diff={audioSkew} adiff={audioSkewAvg} "
                            + $"sample_diff={wantedAudioSampleCount - audioSampleCount} apts={DecodedAudioClockPosition} {AudioSkewThreshold}\n");
                    }
                }
                else
                {
                    /* too big difference : may be initial PTS errors, so
                       reset A-V filter */
                    AudioSkewAvgCount = 0;
                    AudioSkewCummulative = 0;
                }
            }

            return wantedAudioSampleCount;
        }

        #endregion

        #region Action Processing

        internal void RequestSeekToStart()
        {
            RequestSeekTo(Player.MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? Player.MediaStartTimestamp : 0, 0, false);
        }

        /// <summary>
        /// 
        /// Port of goto do_seek
        /// </summary>
        /// <param name="increment"></param>
        internal void RequestSeekBy(double secondsIncrement)
        {
            var currentPos = -1d;
            if (Player.MediaSeekByBytes.HasValue && Player.MediaSeekByBytes.Value == true)
            {
                currentPos = -1;
                if (currentPos < 0 && VideoStreamIndex >= 0)
                    currentPos = VideoQueue.StreamPosition;

                if (currentPos < 0 && AudioStreamIndex >= 0)
                    currentPos = AudioQueue.StreamPosition;

                if (currentPos < 0)
                    currentPos = ffmpeg.avio_tell(InputContext->pb);

                if (InputContext->bit_rate != 0)
                    secondsIncrement *= InputContext->bit_rate / 8.0;
                else
                    secondsIncrement *= 180000;

                currentPos += secondsIncrement;
                RequestSeekTo((long)currentPos, (long)secondsIncrement, true);
            }
            else
            {
                currentPos = MasterClockPositionSeconds;
                if (double.IsNaN(currentPos))
                    currentPos = (double)SeekTargetPosition / ffmpeg.AV_TIME_BASE;

                currentPos += secondsIncrement;

                if (InputContext->start_time != ffmpeg.AV_NOPTS_VALUE && currentPos < InputContext->start_time / (double)ffmpeg.AV_TIME_BASE)
                    currentPos = InputContext->start_time / (double)ffmpeg.AV_TIME_BASE;

                RequestSeekTo((long)(currentPos * ffmpeg.AV_TIME_BASE), (long)(secondsIncrement * ffmpeg.AV_TIME_BASE), false);
            }
        }

        internal void RequestSeekTo(long pos, long rel, bool seekByBytes)
        {
            if (IsSeekRequested) return;

            SeekTargetPosition = pos;
            SeekTargetRange = rel;
            SeekModeFlags &= ~ffmpeg.AVSEEK_FLAG_BYTE;
            if (seekByBytes)
                SeekModeFlags |= ffmpeg.AVSEEK_FLAG_BYTE;

            IsSeekRequested = true;
            IsFrameDecoded.Signal();
        }

        internal void SeekChapter(int increment)
        {
            long pos = Convert.ToInt64(MasterClockPositionSeconds * ffmpeg.AV_TIME_BASE);
            int i = 0;

            if (InputContext->nb_chapters == 0)
                return;

            for (i = 0; i < InputContext->nb_chapters; i++)
            {
                AVChapter* ch = InputContext->chapters[i];
                if (ffmpeg.av_compare_ts(pos, ffmpeg.AV_TIME_BASE_Q, ch->start, ch->time_base) < 0)
                {
                    i--;
                    break;
                }
            }

            i += increment;
            i = Math.Max(i, 0);
            if (i >= InputContext->nb_chapters)
                return;

            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Seeking to chapter {i}.\n");
            RequestSeekTo(ffmpeg.av_rescale_q(InputContext->chapters[i]->start, InputContext->chapters[i]->time_base,
                                         ffmpeg.AV_TIME_BASE_Q), 0, false);
        }

        internal void CycleStreamChannel(AVMediaType mediaType)
        {
            var ic = InputContext;

            int startIndex;
            int targetIndex;
            int prevIndex;

            AVStream* st;
            AVProgram* program = null;

            int streamCount = (int)InputContext->nb_streams;

            if (mediaType == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                startIndex = LastVideoStreamIndex;
                prevIndex = VideoStreamIndex;
            }
            else if (mediaType == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                startIndex = LastAudioStreamIndex;
                prevIndex = AudioStreamIndex;
            }
            else
            {
                startIndex = LastSubtitleStreamIndex;
                prevIndex = SubtitleStreamIndex;
            }

            targetIndex = startIndex;
            if (mediaType != AVMediaType.AVMEDIA_TYPE_VIDEO && VideoStreamIndex != -1)
            {
                program = ffmpeg.av_find_program_from_stream(ic, null, VideoStreamIndex);
                if (program != null)
                {
                    streamCount = (int)program->nb_stream_indexes;

                    for (startIndex = 0; startIndex < streamCount; startIndex++)
                        if (program->stream_index[startIndex] == targetIndex)
                            break;

                    if (startIndex == streamCount)
                        startIndex = -1;

                    targetIndex = startIndex;
                }
            }

            while (true)
            {
                if (++targetIndex >= streamCount)
                {
                    if (mediaType == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    {
                        targetIndex = -1;
                        LastSubtitleStreamIndex = -1;
                        break;
                    }

                    if (startIndex == -1)
                        return;

                    targetIndex = 0;
                }

                if (targetIndex == startIndex)
                    return;

                st = InputContext->streams[program != null ? (int)program->stream_index[targetIndex] : targetIndex];
                if (st->codecpar->codec_type == mediaType)
                {
                    var exitLoop = false;
                    switch (mediaType)
                    {
                        case AVMediaType.AVMEDIA_TYPE_AUDIO:
                            if (st->codecpar->sample_rate != 0 && st->codecpar->channels != 0)
                                exitLoop = true;
                            break;
                        case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                            exitLoop = true;
                            break;
                        default:
                            break;
                    }

                    if (exitLoop) break;
                }
            }

            // the_end:
            if (program != null && targetIndex != -1)
                targetIndex = (int)program->stream_index[targetIndex];

            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, $"Switch {ffmpeg.av_get_media_type_string(mediaType)} stream from #{prevIndex} to #{targetIndex}\n");

            CloseStreamComponent(prevIndex);
            OpenStreamComponent(targetIndex);
        }

        internal void StreamTogglePause()
        {
            if (IsPaused)
            {
                VideoFrameTimeSeconds += ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE - VideoClock.LastUpdatedSeconds;
                if (ReadPauseResult != ffmpeg.AVERROR_NOTSUPP)
                {
                    VideoClock.IsPaused = false;
                }

                VideoClock.SetPosition(VideoClock.PositionSeconds, VideoClock.PacketSerial);
            }

            ExternalClock.SetPosition(ExternalClock.PositionSeconds, ExternalClock.PacketSerial);
            IsPaused = AudioClock.IsPaused = VideoClock.IsPaused = ExternalClock.IsPaused = !IsPaused;
        }

        internal void TogglePause()
        {
            StreamTogglePause();
            IsFrameStepping = false;
        }

        internal void ToggleMute()
        {
            IsAudioMuted = !IsAudioMuted;
        }

        internal void UpdateVolume(int sign, int step)
        {
            AudioVolume = ffmpeg.av_clip(AudioVolume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        internal void StepToNextFrame()
        {
            if (IsPaused) StreamTogglePause();
            IsFrameStepping = true;
        }

        #endregion

    }

}
