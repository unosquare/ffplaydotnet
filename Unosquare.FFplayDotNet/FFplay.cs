namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Unosquare.FFplayDotNet.Core;
    using Unosquare.FFplayDotNet.Primitives;
    using static Unosquare.FFplayDotNet.SDL;

    // https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c

    public unsafe partial class FFplay
    {

        #region Public Events

        public event EventHandler<VideoDataEventArgs> OnVideoDataAvailable;
        public event EventHandler<SubtitleDataEventArgs> OnSubtitleDataAvailable;

        #endregion

        #region Internal State Management

        internal readonly MediaState State = null;
        internal long RednerAudioCallbackTimestamp;
        internal long LastVideoRefreshTimestamp;
        internal bool LogStatusMessages = true;

        internal readonly Task MediaReadTask;
        internal readonly Task ProcessActionsTask;
        internal readonly MediaActionQueue ActionQueue = new MediaActionQueue();

        //internal delegate int InterruptCallbackDelegate(void* opaque);
        //internal readonly InterruptCallbackDelegate OnDecodeInterruptCallback = new InterruptCallbackDelegate(HandleDecodeInterrupt);

        /// <summary>
        /// Port of format_opts
        /// </summary>
        internal readonly FFDictionary FormatOptions = null;

        #endregion

        #region Public, End-User Properties

        public PlayerOptions Options { get; private set; }
        public string MediaTitle { get; private set; }
        public AVInputFormat* InputForcedFormat { get; private set; }
        public long MediaStartTimestamp { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public long MediaDuration { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public bool? MediaSeekByBytes { get; private set; } = null;
        public string AudioCodecName { get; private set; }
        public string SubtitleCodecName { get; private set; }
        public string VideoCodecName { get; private set; }

        #endregion

        #region Constructor and Destructor

        /// <summary>
        /// Initializes a new instance of the <see cref="FFplay"/> class.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <param name="formatName">Name of the fromat. Leave null for automatic selection</param>
        public FFplay(string filename, string formatName = null)
        {
            Options = new PlayerOptions
            {
                MediaInputUrl = filename,
                InputFormatName = formatName
            };

            Helper.RegisterFFmpeg();

            // Inlining init_opts

            FormatOptions = new FFDictionary();
            FormatOptions.Fill(Options.FormatOptions);

            #region main

            ffmpeg.avformat_network_init();

            if (string.IsNullOrWhiteSpace(Options.InputFormatName) == false)
                InputForcedFormat = ffmpeg.av_find_input_format(Options.InputFormatName);

            State = new MediaState(this);

            if (InitializeInputReader())
            {
                MediaReadTask = Task.Run(() => { ReaderTaskDoWork(); });
                ProcessActionsTask = Task.Run(() => { ProcessActionQueueContinuously(); });
            }


            #endregion
        }

        /// <summary>
        /// Releases Unmanaged resources and closes all streams
        /// Port of do_exit
        /// </summary>
        private void Close()
        {
            // TODO: Maybe change this method to be the Dispose method?
            State?.CloseStream();
            ActionQueue.Clear();

            //ffmpeg.av_lockmgr_register(null);
            ffmpeg.avformat_network_deinit();
            ffmpeg.av_log(null, ffmpeg.AV_LOG_QUIET, "");
        }

        #endregion

        #region FFmpeg Locking and Interupts


        /// <summary>
        /// This gets called by ffmpeg upon decoding frames
        /// Port of decode_interrupt_cb
        /// </summary>
        /// <param name="opaque"></param>
        /// <returns></returns>
        private static int HandleDecodeInterrupt(void* opaque)
        {
            var stateHandle = GCHandle.FromIntPtr(new IntPtr(opaque));
            if (stateHandle.IsAllocated && stateHandle.Target != null)
                return (stateHandle.Target as MediaState).IsAbortRequested ? 1 : 0;

            return 1;
        }

        #endregion

        #region Audio Rendering

        /// <summary>
        /// Opens the audio device with the provided parameters
        /// Port of audio_open
        /// </summary>
        /// <param name="wantedChannelLayout"></param>
        /// <param name="wantedChannelCount"></param>
        /// <param name="wantedSampleRate"></param>
        /// <param name="audioHardware"></param>
        /// <returns></returns>
        internal int audio_open(long wantedChannelLayout, int wantedChannelCount, int wantedSampleRate, AudioParams audioHardware)
        {

            var spec = new SDL_AudioSpec();

            var channelCountOptions = new int[] { 0, 0, 1, 6, 2, 6, 4, 6 };
            var sampleRateOptions = new int[] { 0, 44100, 48000, 96000, 192000 };
            int sampleRateId = sampleRateOptions.Length - 1;

            var audioChannelsEnv = SDL_getenv("SDL_AUDIO_CHANNELS");

            if (string.IsNullOrWhiteSpace(audioChannelsEnv) == false)
            {
                wantedChannelCount = int.Parse(audioChannelsEnv);
                wantedChannelLayout = ffmpeg.av_get_default_channel_layout(wantedChannelCount);
            }

            if (wantedChannelLayout == 0 || wantedChannelCount != ffmpeg.av_get_channel_layout_nb_channels((ulong)wantedChannelLayout))
            {
                wantedChannelLayout = ffmpeg.av_get_default_channel_layout(wantedChannelCount);
                wantedChannelLayout &= ~ffmpeg.AV_CH_LAYOUT_STEREO_DOWNMIX;
            }

            wantedChannelCount = ffmpeg.av_get_channel_layout_nb_channels((ulong)wantedChannelLayout);

            var wantedAudioSpec = new SDL_AudioSpec();
            wantedAudioSpec.channels = wantedChannelCount;
            wantedAudioSpec.freq = wantedSampleRate;
            if (wantedAudioSpec.freq <= 0 || wantedAudioSpec.channels <= 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "Invalid sample rate or channel count!\n");
                return -1;
            }

            while (sampleRateId != 0 && sampleRateOptions[sampleRateId] >= wantedAudioSpec.freq)
                sampleRateId--;

            wantedAudioSpec.format = AUDIO_S16SYS;
            wantedAudioSpec.silence = 0;
            wantedAudioSpec.samples = Math.Max(
                Constants.SDL_AUDIO_MIN_BUFFER_SIZE,
                2 << ffmpeg.av_log2(Convert.ToUInt32(wantedAudioSpec.freq / Constants.SDL_AUDIO_MAX_CALLBACKS_PER_SEC)));
            wantedAudioSpec.callback = new SDL_AudioCallback(RednerAudioCallback);
            wantedAudioSpec.userdata = State;

            while (SDL_OpenAudio(wantedAudioSpec, spec) < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"SDL_OpenAudio ({wantedAudioSpec.channels} channels, {wantedAudioSpec.freq} Hz): {SDL_GetError()}\n");

                wantedAudioSpec.channels = channelCountOptions[Math.Min(7, wantedAudioSpec.channels)];
                if (wantedAudioSpec.channels == 0)
                {
                    wantedAudioSpec.freq = sampleRateOptions[sampleRateId--];
                    wantedAudioSpec.channels = wantedChannelCount;
                    if (wantedAudioSpec.freq == 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               "No more combinations to try, audio open failed\n");
                        return -1;
                    }
                }
                wantedChannelLayout = ffmpeg.av_get_default_channel_layout(wantedAudioSpec.channels);
            }
            if (spec.format != AUDIO_S16SYS)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                       $"SDL advised audio format {spec.format} is not supported!\n");
                return -1;
            }

            if (spec.channels != wantedAudioSpec.channels)
            {
                wantedChannelLayout = ffmpeg.av_get_default_channel_layout(spec.channels);
                if (wantedChannelLayout == 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                           $"SDL advised channel count {spec.channels} is not supported!\n");
                    return -1;
                }
            }

            audioHardware.SampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            audioHardware.Frequency = spec.freq;
            audioHardware.ChannelLayout = wantedChannelLayout;
            audioHardware.ChannelCount = spec.channels;
            audioHardware.SampleBufferLength = ffmpeg.av_samples_get_buffer_size(null, audioHardware.ChannelCount, 1, audioHardware.SampleFormat, 1);
            audioHardware.BytesPerSecond = ffmpeg.av_samples_get_buffer_size(null, audioHardware.ChannelCount, audioHardware.Frequency, audioHardware.SampleFormat, 1);

            if (audioHardware.BytesPerSecond <= 0 || audioHardware.SampleBufferLength <= 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size failed\n");
                return -1;
            }

            return spec.size;
        }

        /// <summary>
        /// Port of sdl_audio_callback
        /// </summary>
        /// <param name="vst"></param>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        private void RednerAudioCallback(MediaState vst, byte* stream, int length)
        {
            RednerAudioCallbackTimestamp = ffmpeg.av_gettime_relative();

            while (length > 0)
            {
                if (vst.RenderAudioBufferIndex >= vst.RenderAudioBufferLength)
                {
                    var bufferLength = vst.UncompressAudioFrame();
                    if (bufferLength < 0)
                    {
                        vst.RenderAudioBuffer = null;
                        vst.RenderAudioBufferLength = Convert.ToUInt32(Constants.SDL_AUDIO_MIN_BUFFER_SIZE / vst.AudioOutputParams.SampleBufferLength * vst.AudioOutputParams.SampleBufferLength);
                    }
                    else
                    {
                        vst.RenderAudioBufferLength = Convert.ToUInt32(bufferLength);
                    }

                    vst.RenderAudioBufferIndex = 0;
                }

                var pendingAudioBytes = Convert.ToInt32(vst.RenderAudioBufferLength - vst.RenderAudioBufferIndex);
                if (pendingAudioBytes > length) pendingAudioBytes = length;

                if (!vst.IsAudioMuted && vst.RenderAudioBuffer != null && vst.AudioVolume == SDL_MIX_MAXVOLUME)
                {
                    Native.memcpy(stream, vst.RenderAudioBuffer + vst.RenderAudioBufferIndex, pendingAudioBytes);
                }
                else
                {
                    Native.memset(stream, 0, pendingAudioBytes);
                    if (!vst.IsAudioMuted && vst.RenderAudioBuffer != null)
                        SDL_MixAudio(stream, vst.RenderAudioBuffer + vst.RenderAudioBufferIndex, pendingAudioBytes, vst.AudioVolume);
                }

                length -= pendingAudioBytes;
                stream += pendingAudioBytes;
                vst.RenderAudioBufferIndex += pendingAudioBytes;
            }

            if (!double.IsNaN(vst.DecodedAudioClockPosition))
            {
                var pendingAudioBytes = Convert.ToInt32(vst.RenderAudioBufferLength - vst.RenderAudioBufferIndex);
                vst.AudioClock.SetPosition(
                    vst.DecodedAudioClockPosition - (double)(2 * vst.AudioHardwareBufferSize + pendingAudioBytes) / vst.AudioOutputParams.BytesPerSecond,
                    vst.DecodedAudioClockSerial, RednerAudioCallbackTimestamp / (double)ffmpeg.AV_TIME_BASE);

                vst.ExternalClock.SyncTo(vst.AudioClock);
            }
        }

        #endregion

        #region Video and Subtitle Rendering

        /// <summary>
        /// Raises events providing video and subtitle rendering data to the
        /// event subsribers
        /// Port of video_image_display
        /// </summary>
        private void BroadcastVideoAndSubtitlesDataEvents()
        {
            var videoFrame = State.VideoQueue.Last;
            FrameHolder subtitleFrame = null;

            if (videoFrame.Bitmap == null)
                return;

            if (State.SubtitleStream != null)
            {
                if (State.SubtitleQueue.PendingCount > 0)
                {
                    subtitleFrame = State.SubtitleQueue.Current;
                    var subtitleStartDisplayTime = subtitleFrame.PtsSeconds + ((float)subtitleFrame.Subtitle->start_display_time / 1000);
                    if (videoFrame.PtsSeconds >= subtitleStartDisplayTime)
                    {
                        // We are not going to be rendering subtitle textures. I removed the code to render the subtitles
                        if (subtitleFrame.IsUploaded == false)
                            subtitleFrame.IsUploaded = true;

                    }
                    else
                    {
                        subtitleFrame = null;
                    }
                }
            }

            if (videoFrame.IsUploaded == false)
            {
                if (State.UncompressVideoFrame(videoFrame) == false)
                    return;

                videoFrame.IsUploaded = true;
            }

            // Combine Video and Subtitle textures
            RaiseOnVideoDataAvailable(videoFrame); // Port: Previoulsy just a call to SDL_RenderCopy(renderer, videoFrame.Bitmap, null, rect);
            RaiseOnSubtitleDataAvailable(subtitleFrame); // Port:  Previoulsy just a call to SDL_RenderCopy

        }

        /// <summary>
        /// Refreshes the video frame data and returns the remaining time
        /// in seconds to sleep before a new frame is required to be presented.
        /// Port of video_refresh
        /// </summary>
        /// <param name="remainingSeconds">The remaining time.</param>
        /// <returns></returns>
        private double RefreshVideoAndSubtitles(double remainingSeconds)
        {
            if (State.IsPaused == false && State.MasterSyncMode == SyncMode.External && State.IsMediaRealtime)
                State.AdjustExternalClockSpeedRatio();

            if (State.VideoStream != null)
            {

                var retry = true;

                while (retry)
                {
                    retry = false;

                    // check if we have a picture to display in the queue
                    // otherwise, there is nothing to do, no picture to display in the queue
                    if (State.VideoQueue.PendingCount <= 0)
                        break;

                    var lastVideoFrame = State.VideoQueue.Last;
                    var currentVideoFrame = State.VideoQueue.Current;

                    // Catch up with the read stream
                    if (currentVideoFrame.Serial != State.VideoPackets.Serial)
                    {
                        State.VideoQueue.QueueNextRead();
                        retry = true;
                        continue;
                    }

                    if (lastVideoFrame.Serial != currentVideoFrame.Serial)
                        State.VideoFrameTimeSeconds = (double)ffmpeg.av_gettime_relative() / ffmpeg.AV_TIME_BASE;

                    if (State.IsPaused)
                        break;

                    var lastFrameDuration = State.ComputeVideoFrameDurationSeconds(lastVideoFrame, currentVideoFrame);
                    var delaySeconds = State.ComputeVideoClockDelay(lastFrameDuration);
                    var currentTimeSeconds = ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE;

                    if (currentTimeSeconds < State.VideoFrameTimeSeconds + delaySeconds)
                    {
                        remainingSeconds = Math.Min(State.VideoFrameTimeSeconds + delaySeconds - currentTimeSeconds, remainingSeconds);
                        break;
                    }

                    State.VideoFrameTimeSeconds += delaySeconds;
                    if (delaySeconds > 0 && currentTimeSeconds - State.VideoFrameTimeSeconds > Constants.AvSyncThresholdMaxSecs)
                        State.VideoFrameTimeSeconds = currentTimeSeconds;

                    try
                    {
                        State.VideoQueue.SyncLock.Lock();
                        if (!double.IsNaN(currentVideoFrame.PtsSeconds))
                            State.UpdateVideoPts(currentVideoFrame.PtsSeconds, currentVideoFrame.BytePosition, currentVideoFrame.Serial);
                    }
                    finally
                    {
                        State.VideoQueue.SyncLock.Unlock();
                    }

                    if (State.VideoQueue.PendingCount > 1)
                    {
                        var durationSeconds = State.ComputeVideoFrameDurationSeconds(
                            currentVideoFrame, State.VideoQueue.Next);

                        if (!State.IsFrameStepping
                            && Options.EnableFrameDrops
                            && currentTimeSeconds > State.VideoFrameTimeSeconds + durationSeconds)
                        {
                            State.VideoFrameLateDrops++;
                            State.VideoQueue.QueueNextRead();
                            retry = true;
                            continue;
                        }
                    }

                    // Queue Subtitle Reads
                    if (State.SubtitleStream != null)
                    {
                        while (State.SubtitleQueue.PendingCount > 0)
                        {
                            var currentSubtitleFrame = State.SubtitleQueue.Current;
                            var nextSubtitleFrame = State.SubtitleQueue.PendingCount > 1 ? State.SubtitleQueue.Next : null;

                            if (currentSubtitleFrame.Serial != State.SubtitlePackets.Serial
                                    || (State.VideoClock.PtsSeconds > (currentSubtitleFrame.PtsSeconds + ((float)currentSubtitleFrame.Subtitle->end_display_time / 1000)))
                                    || (nextSubtitleFrame != null && State.VideoClock.PtsSeconds > (nextSubtitleFrame.PtsSeconds + ((float)nextSubtitleFrame.Subtitle->start_display_time / 1000))))
                            {
                                // The code here was removed. It used to lock the texture and
                                // clear all the pixels in the previously created subtitle_texture
                                State.SubtitleQueue.QueueNextRead();
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    State.VideoQueue.QueueNextRead();
                    State.IsVideoRefreshRequested = true;
                    if (State.IsFrameStepping && !State.IsPaused)
                        State.StreamTogglePause();


                }

                // Finally let's send the subtitle and image data as events if required
                if (State.IsVideoRefreshRequested && State.VideoQueue.ReadIndexShown != 0)
                {
                    // video_display // Port: previously just a call to Video display and SDL-related methods
                    BroadcastVideoAndSubtitlesDataEvents();
                }

            }

            State.IsVideoRefreshRequested = false;

            var currentTimestamp = ffmpeg.av_gettime_relative();
            if (LogStatusMessages && (LastVideoRefreshTimestamp == 0 || (currentTimestamp - LastVideoRefreshTimestamp) >= 30000))
            {
                var audioQueueSize = 0;
                var videoQueueSize = 0;
                var subtitleQueueSize = 0;

                if (State.AudioStream != null)
                    audioQueueSize = State.AudioPackets.ByteLength;

                if (State.VideoStream != null)
                    videoQueueSize = State.VideoPackets.ByteLength;

                if (State.SubtitleStream != null)
                    subtitleQueueSize = State.SubtitlePackets.ByteLength;

                var clockSkew = 0d;

                if (State.AudioStream != null && State.VideoStream != null)
                    clockSkew = State.AudioClock.PositionSeconds - State.VideoClock.PositionSeconds;
                else if (State.VideoStream != null)
                    clockSkew = State.MasterClockPositionSeconds - State.VideoClock.PositionSeconds;
                else if (State.AudioStream != null)
                    clockSkew = State.MasterClockPositionSeconds - State.AudioClock.PositionSeconds;

                var mode = (State.AudioStream != null && State.VideoStream != null) ?
                    "A-V" : (State.VideoStream != null ? "M-V" : (State.AudioStream != null ? "M-A" : "   "));
                var faultyDts = State.VideoStream != null ? State.VideoDecoder.Codec->pts_correction_num_faulty_dts : 0;
                var faultyPts = State.VideoStream != null ? State.VideoDecoder.Codec->pts_correction_num_faulty_pts : 0;

                ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO,
                       $"{State.MasterClockPositionSeconds} {mode}:{clockSkew} fd={State.VideoFrameEarlyDrops + State.VideoFrameLateDrops} aq={audioQueueSize / 1024}KB " +
                       $"vq={videoQueueSize / 1024}KB sq={subtitleQueueSize}dB f={faultyDts} / {faultyPts}\r");
            }

            LastVideoRefreshTimestamp = currentTimestamp;
            return remainingSeconds;
        }

        /// <summary>
        /// Prepares the next writable frame to be uploaded (Bitmap-filled).
        /// Port of queue_picture
        /// </summary>
        /// <param name="sourceFrame"></param>
        /// <param name="pts"></param>
        /// <param name="duration"></param>
        /// <returns></returns>
        private int EnqueueVideoFrameBitmapFill(AVFrame* sourceFrame, double pts, double duration)
        {
            var videoFrame = State.VideoQueue.PeekWritableFrame();
            videoFrame.MediaType = AVMediaType.AVMEDIA_TYPE_VIDEO;

            var serial = State.VideoDecoder.PacketSerial;
            var streamPosition = ffmpeg.av_frame_get_pkt_pos(sourceFrame);

            if (Debugger.IsAttached)
                Debug.WriteLine($"frame_type={ffmpeg.av_get_picture_type_char(sourceFrame->pict_type)} pts={pts}");

            if (videoFrame == null)
                return -1;

            videoFrame.PictureAspectRatio = sourceFrame->sample_aspect_ratio;
            videoFrame.IsUploaded = false;

            if (videoFrame.Bitmap == null ||
                videoFrame.PictureWidth != sourceFrame->width ||
                videoFrame.PictureHeight != sourceFrame->height ||
                videoFrame.format != sourceFrame->format)
            {
                videoFrame.PictureWidth = sourceFrame->width;
                videoFrame.PictureHeight = sourceFrame->height;
                videoFrame.format = sourceFrame->format;

                try
                {
                    /* wait until the picture is allocated */
                    State.VideoQueue.SyncLock.Lock();
                    if (State.VideoPackets.IsAborted == false)
                        State.VideoQueue.IsDoneWriting.Wait(State.VideoQueue.SyncLock);

                }
                finally
                {
                    State.VideoQueue.SyncLock.Unlock();
                }

                if (State.VideoPackets.IsAborted)
                    return -1;
            }

            if (videoFrame.Bitmap != null)
            {
                videoFrame.PtsSeconds = pts;
                videoFrame.DurationSeconds = duration;
                videoFrame.BytePosition = streamPosition;
                videoFrame.Serial = serial;

                ffmpeg.av_frame_move_ref(videoFrame.DecodedFrame, sourceFrame);
                State.VideoQueue.QueueNextWrite();
            }

            return 0;
        }

        #endregion

        #region Decoding Tasks: Frames Queues for audio, video, and subtitles

        /// <summary>
        /// Continuously decodes video frames from the video frame queue
        /// using the video decoder.
        /// Port of video_thread
        /// </summary>
        /// <param name="mediaState">The media state object</param>
        internal static void DecodeVideoQueueContinuously(MediaState mediaState)
        {
            var decodedFrame = ffmpeg.av_frame_alloc();
            var frameTimebase = mediaState.VideoStream->time_base;
            var frameRate = ffmpeg.av_guess_frame_rate(mediaState.InputContext, mediaState.VideoStream, null);

            while (true)
            {
                var result = mediaState.VideoDecoder.Decode(decodedFrame);

                if (result < 0) break;
                if (result == 0) continue;

                var framePts = (decodedFrame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : decodedFrame->pts * ffmpeg.av_q2d(frameTimebase);
                var frameDuration = frameRate.num != 0 && frameRate.den != 0 ?
                    ffmpeg.av_q2d(new AVRational { num = frameRate.den, den = frameRate.num }) : 0;

                result = mediaState.Player.EnqueueVideoFrameBitmapFill(decodedFrame, framePts, frameDuration);
                ffmpeg.av_frame_unref(decodedFrame);

                if (result < 0) break;
            }

            ffmpeg.av_frame_free(&decodedFrame);
        }

        /// <summary>
        /// Continuously decodes audio frames from the audio frame queue
        /// using the audio decoder.
        /// Port of audio_thread
        /// </summary>
        /// <param name="mediaState">The media state object.</param>
        internal static void DecodeAudioQueueContinuously(MediaState mediaState)
        {
            var decodedFrame = ffmpeg.av_frame_alloc();
            int gotFrame = 0;
            int ret = 0;

            do
            {
                gotFrame = mediaState.AudioDecoder.Decode(decodedFrame);

                if (gotFrame < 0) break;

                if (gotFrame != 0)
                {
                    var timeBase = new AVRational { num = 1, den = decodedFrame->sample_rate };
                    var audioFrame = mediaState.AudioQueue.PeekWritableFrame();
                    audioFrame.MediaType = AVMediaType.AVMEDIA_TYPE_AUDIO;

                    if (audioFrame == null) break;

                    audioFrame.PtsSeconds = (decodedFrame->pts == ffmpeg.AV_NOPTS_VALUE) ? double.NaN : decodedFrame->pts * ffmpeg.av_q2d(timeBase);
                    audioFrame.BytePosition = ffmpeg.av_frame_get_pkt_pos(decodedFrame);
                    audioFrame.Serial = mediaState.AudioDecoder.PacketSerial;
                    audioFrame.DurationSeconds = ffmpeg.av_q2d(new AVRational { num = decodedFrame->nb_samples, den = decodedFrame->sample_rate });

                    ffmpeg.av_frame_move_ref(audioFrame.DecodedFrame, decodedFrame);
                    mediaState.AudioQueue.QueueNextWrite();

                }
            } while (ret >= 0 || ret == ffmpeg.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF);

            ffmpeg.av_frame_free(&decodedFrame);
        }

        /// <summary>
        /// Continuously decodes the subtitle frame queue by getting the
        /// next available writable frame in the queue, and then queues the next write
        /// Port of subtitle_thread
        /// </summary>
        /// <param name="mediaState">The media state object</param>
        internal static void DecodeSubtitlesQueueContinuously(MediaState mediaState)
        {
            var hasDecoded = 0;

            while (true)
            {
                var decodedFrame = mediaState.SubtitleQueue.PeekWritableFrame();
                decodedFrame.MediaType = AVMediaType.AVMEDIA_TYPE_SUBTITLE;

                if (decodedFrame == null)
                    return;

                hasDecoded = mediaState.SubtitleDecoder.Decode(ref decodedFrame.Subtitle);

                if (hasDecoded < 0)
                    break;

                if (hasDecoded != 0 && decodedFrame.Subtitle->format == 0)
                {
                    decodedFrame.PtsSeconds = (decodedFrame.Subtitle->pts != ffmpeg.AV_NOPTS_VALUE) ?
                        decodedFrame.Subtitle->pts / (double)ffmpeg.AV_TIME_BASE : 0;
                    decodedFrame.Serial = mediaState.SubtitleDecoder.PacketSerial;
                    decodedFrame.PictureWidth = mediaState.SubtitleDecoder.Codec->width;
                    decodedFrame.PictureHeight = mediaState.SubtitleDecoder.Codec->height;
                    decodedFrame.IsUploaded = false;

                    /* now we can update the picture count */
                    mediaState.SubtitleQueue.QueueNextWrite();
                }
                else if (hasDecoded != 0)
                {
                    ffmpeg.avsubtitle_free(decodedFrame.Subtitle);
                }
            }
        }

        #endregion

        #region Input Reader Initialization, Looping and Action Processing

        /// <summary>
        /// Initializes the state variables and input context
        /// for the reader thread to start the read loop.
        /// This is the first half of the login in the read_thread method.
        /// Note wanted_stream_specs related code was completely removed.
        /// </summary>
        /// <returns></returns>
        private bool InitializeInputReader()
        {
            #region Initial setup and state variables

            var result = default(int);
            FFDictionaryEntry optionEntry;
            var streamIndexes = new int[Constants.AVMEDIA_TYPE_COUNT];

            {
                Native.memset(streamIndexes, -1, streamIndexes.Length);
                State.LastVideoStreamIndex = State.VideoStreamIndex = -1;
                State.LastAudioStreamIndex = State.AudioStreamIndex = -1;
                State.LastSubtitleStreamIndex = State.SubtitleStreamIndex = -1;
                State.IsAtEndOfFile = false;
            }


            #endregion

            #region Create the Input Format Context and Set the Format Options

            // Allocate and set the interrupts
            {
                State.InputContext = ffmpeg.avformat_alloc_context();

                //var inputContext = State.InputContext;

                // TODO: Maybe manual interrupt callbacks are not even necessary. Tempted to remove them
                //inputContext->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(OnDecodeInterruptCallback) };
                //inputContext->interrupt_callback.opaque = (void*)State.Handle.AddrOfPinnedObject();

                // For streams that support scanning PMTs, set it to 1
                if (FormatOptions.HasKey("scan_all_pmts") == false)
                    FormatOptions.Set("scan_all_pmts", "1", true);

                // Open the assigned input context
                AVFormatContext* inputContext; // Input context gets created in unmanaged code.
                var openResult = ffmpeg.avformat_open_input(&inputContext, State.MediaUrl, State.InputFormat, FormatOptions.Reference);
                if (openResult < 0)
                {
                    Debug.WriteLine($"Error in read_thread. File '{State.MediaUrl}'. {openResult}");
                    // TODO: throw open exception
                    return false;
                }

                State.InputContext = inputContext;

                FormatOptions.Remove("scan_all_pmts");

                // On return FormatOptions will be filled with options that were not found.
                if ((optionEntry = FormatOptions.First()) != null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"Format Option '{optionEntry.Key}' not found.\n");
                    // Don't fail just because an option was not found
                    //return false; // ffmpeg.AVERROR_OPTION_NOT_FOUND;
                }
            }

            #endregion

            #region Set Codec Parameters
            {
                var inputContext = State.InputContext;

                if (Options.GeneratePts) inputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
                ffmpeg.av_format_inject_global_side_data(inputContext);

                var streamOptions = Options.CodecOptions.GetPerStreamOptions(inputContext);
                var inputStreamCount = Convert.ToInt32(inputContext->nb_streams);

                // Build the options array, one dictionary per stream
                var streamOptionsArr = new IntPtr[streamOptions.Length];
                for (var i = 0; i < streamOptions.Length; i++)
                    streamOptionsArr[i] = new IntPtr(streamOptions[i].Pointer);

                var streamOptionsHandle = GCHandle.Alloc(streamOptionsArr, GCHandleType.Pinned);
                var findStreamInfoResult = ffmpeg.avformat_find_stream_info(inputContext, (AVDictionary**)streamOptionsHandle.AddrOfPinnedObject());
                streamOptionsHandle.Free();

                // Memory management nor required. (removed 2 blocks with ffmpeg.av_dict_free and av_freep
                if (findStreamInfoResult < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{State.MediaUrl}: could not find codec parameters\n");
                    // Dont't fail just because a Codec option was not validated
                    //return false; // -1;
                }
            }
            #endregion

            #region Extract Initial State Properties
            {
                var inputContext = State.InputContext;
                if (inputContext->pb != null)
                    inputContext->pb->eof_reached = 0; // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end

                var formatName = Native.BytePtrToString(inputContext->iformat->name);
                var isDiscontinuous = (inputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;

                // seek by byes only for continuous ogg vorbis
                if (MediaSeekByBytes.HasValue == false)
                    MediaSeekByBytes = isDiscontinuous && formatName.Equals("ogg") == false;

                // Set max frame duration depending on discontinuity
                State.MaximumFrameDuration = isDiscontinuous ? 10.0 : 3600.0;

                // Set the media title if the metadata contains it
                optionEntry = FFDictionary.GetEntry(inputContext->metadata, "title", false);
                MediaTitle = optionEntry?.Value;
            }

            #endregion

            #region Seek to the initial point of the media start time
            {
                var inputContext = State.InputContext;
                if (MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE)
                {
                    var timestamp = MediaStartTimestamp;
                    if (inputContext->start_time != ffmpeg.AV_NOPTS_VALUE)
                        timestamp += inputContext->start_time;

                    // Seek to start time within the media stream
                    result = ffmpeg.avformat_seek_file(inputContext, -1, long.MinValue, timestamp, long.MaxValue, 0);

                    if (result < 0)
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{State.MediaUrl}: could not seek to position {((double)timestamp / ffmpeg.AV_TIME_BASE)}\n");
                }

                if (LogStatusMessages)
                    ffmpeg.av_dump_format(inputContext, 0, State.MediaUrl, 0);
            }


            #endregion

            #region Find the best stream for each stream media component
            {
                var inputContext = State.InputContext;

                if (!Options.IsVideoDisabled)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                        ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);

                if (!Options.IsAudioDisabled)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                        ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                            null, 0);

                if (!Options.IsVideoDisabled && !Options.IsSubtitleDisabled)
                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                        ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                            (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                             streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                             streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                            null, 0);
            }

            #endregion

            #region Removed: Call to windowing

            // vst.show_mode = show_mode;
            // remopved block where set_default_window_size is called.
            // because it is not needed.

            #endregion

            #region Open Video, audio and subtitle streams

            {
                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
                    State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]);

                result = -1;

                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
                    result = State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]);

                if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
                    State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE]);

                if (State.VideoStreamIndex < 0 && State.AudioStreamIndex < 0)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Failed to open file '{State.MediaUrl}'\n");
                    return false; // -1;
                }

                if (Options.EnableInfiniteBuffer < 0 && State.IsMediaRealtime)
                    Options.EnableInfiniteBuffer = 1;
            }

            #endregion

            return result >= 0;
        }

        /// <summary>
        /// Continuously reads from the input stream.
        /// Port of read_thread
        /// </summary>
        /// <returns></returns>
        public int ReaderTaskDoWork()
        {
            var DecoderLock = new MonitorLock();


            // Setup state variables
            var inputContext = State.InputContext;
            var formatName = inputContext != null ? Native.BytePtrToString(inputContext->iformat->name) : string.Empty;

            // Loops by reading and acting on states
            while (true)
            {
                if (State.IsAbortRequested)
                    break;

                if (State.IsPaused != State.WasPaused)
                {
                    State.WasPaused = State.IsPaused;

                    if (State.IsPaused)
                        State.ReadPauseResult = ffmpeg.av_read_pause(inputContext);
                    else
                        ffmpeg.av_read_play(inputContext);
                }

                if (State.IsPaused &&
                    (formatName.Equals("rtsp") || (inputContext->pb != null && Options.MediaInputUrl.StartsWith("mmsh:"))))
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (State.IsSeekRequested)
                {
                    var seekTarget = State.SeekTargetPosition;
                    var seekMin = State.SeekTargetRange > 0 ? seekTarget - State.SeekTargetRange + 2 : long.MinValue;
                    var seekMax = State.SeekTargetRange < 0 ? seekTarget - State.SeekTargetRange - 2 : long.MaxValue;
                    // TODO: the +-2 is due to rounding being not done in the correct direction in generation
                    //      of the seek_pos/seek_rel variables

                    var seekResult = ffmpeg.avformat_seek_file(State.InputContext, -1, seekMin, seekTarget, seekMax, State.SeekModeFlags);

                    if (seekResult < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               $"{Encoding.GetEncoding(0).GetString(State.InputContext->filename)}: error while seeking\n");
                    }
                    else
                    {
                        if (State.AudioStreamIndex >= 0)
                        {
                            State.AudioPackets.Clear();
                            State.AudioPackets.EnqueueFlushPacket();
                        }

                        if (State.SubtitleStreamIndex >= 0)
                        {
                            State.SubtitlePackets.Clear();
                            State.SubtitlePackets.EnqueueFlushPacket();
                        }

                        if (State.VideoStreamIndex >= 0)
                        {
                            State.VideoPackets.Clear();
                            State.VideoPackets.EnqueueFlushPacket();
                        }

                        if ((State.SeekModeFlags & ffmpeg.AVSEEK_FLAG_BYTE) != 0)
                        {
                            State.ExternalClock.SetPosition(double.NaN, 0);
                        }
                        else
                        {
                            State.ExternalClock.SetPosition(seekTarget / (double)ffmpeg.AV_TIME_BASE, 0);
                        }
                    }

                    State.IsSeekRequested = false;
                    State.EnqueuePacketAttachments = true;
                    State.IsAtEndOfFile = false;

                    if (State.IsPaused)
                        State.StepToNextFrame();
                }

                if (State.EnqueuePacketAttachments)
                {
                    if (State.VideoStream != null && (State.VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        var packetCopy = new AVPacket();
                        var copyResult = ffmpeg.av_copy_packet(&packetCopy, &State.VideoStream->attached_pic);
                        if (copyResult < 0) break;

                        State.VideoPackets.Enqueue(&packetCopy);
                        State.VideoPackets.EnqueueEmptyPacket(State.VideoStreamIndex);
                    }

                    State.EnqueuePacketAttachments = false;
                }

                if (Options.EnableInfiniteBuffer < 1 &&
                      (State.AudioPackets.ByteLength + State.VideoPackets.ByteLength + State.SubtitlePackets.ByteLength > PacketQueue.MaxQueueByteLength
                    || (State.AudioPackets.HasEnoughPackets(State.AudioStream, State.AudioStreamIndex) &&
                        State.VideoPackets.HasEnoughPackets(State.VideoStream, State.VideoStreamIndex) &&
                        State.SubtitlePackets.HasEnoughPackets(State.SubtitleStream, State.SubtitleStreamIndex))))
                {
                    try
                    {
                        DecoderLock.Lock();
                        State.IsFrameDecoded.Wait(DecoderLock, 10);
                    }
                    finally
                    {
                        DecoderLock.Unlock();
                    }

                    continue;
                }

                if (!State.IsPaused &&
                    (State.AudioStream == null || (State.AudioDecoder.IsFinished == Convert.ToBoolean(State.AudioPackets.Serial) && State.AudioQueue.PendingCount == 0)) &&
                    (State.VideoStream == null || (State.VideoDecoder.IsFinished == Convert.ToBoolean(State.VideoPackets.Serial) && State.VideoQueue.PendingCount == 0)))
                {

                    // TODO: Raise event Media Finished
                    // if (Reqind afterFinished)
                    //     State.RequestSeekToStart();
                    break;
                }

                var readPacket = new AVPacket();
                var readPacketPtr = &readPacket;
                var readFrameResult = ffmpeg.av_read_frame(inputContext, readPacketPtr);

                if (readFrameResult < 0)
                {
                    if ((readFrameResult == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(inputContext->pb) != 0) && !State.IsAtEndOfFile)
                    {
                        if (State.VideoStreamIndex >= 0)
                            State.VideoPackets.EnqueueEmptyPacket(State.VideoStreamIndex);
                        if (State.AudioStreamIndex >= 0)
                            State.AudioPackets.EnqueueEmptyPacket(State.AudioStreamIndex);
                        if (State.SubtitleStreamIndex >= 0)
                            State.SubtitlePackets.EnqueueEmptyPacket(State.SubtitleStreamIndex);

                        State.IsAtEndOfFile = true;
                    }

                    if (inputContext->pb != null && inputContext->pb->error != 0)
                        break;

                    try
                    {
                        DecoderLock.Lock();
                        State.IsFrameDecoded.Wait(DecoderLock, 10);
                    }
                    finally
                    {
                        DecoderLock.Unlock();
                    }

                    continue;
                }
                else
                {
                    State.IsAtEndOfFile = false;
                }

                var streamStartTimestamp = inputContext->streams[readPacketPtr->stream_index]->start_time;
                var packetTimestamp = readPacketPtr->pts == ffmpeg.AV_NOPTS_VALUE ? readPacketPtr->dts : readPacketPtr->pts;
                var isPacketInPlayRange = MediaDuration == ffmpeg.AV_NOPTS_VALUE ||
                        (packetTimestamp - (streamStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? streamStartTimestamp : 0)) *
                        ffmpeg.av_q2d(inputContext->streams[readPacketPtr->stream_index]->time_base) -
                        (double)(MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0) / ffmpeg.AV_TIME_BASE
                        <= ((double)MediaDuration / ffmpeg.AV_TIME_BASE);

                // Enqueue the read packet depending on the the type of packet
                if (readPacketPtr->stream_index == State.AudioStreamIndex && isPacketInPlayRange)
                    State.AudioPackets.Enqueue(readPacketPtr);
                else if (readPacketPtr->stream_index == State.VideoStreamIndex && isPacketInPlayRange
                    && (State.VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                    State.VideoPackets.Enqueue(readPacketPtr);
                else if (readPacketPtr->stream_index == State.SubtitleStreamIndex && isPacketInPlayRange)
                    State.SubtitlePackets.Enqueue(readPacketPtr);
                else
                    ffmpeg.av_packet_unref(readPacketPtr); // Discard packets that contain other stuff...
            }


            if (State.InputContext != null)
            {
                ffmpeg.avformat_close_input(&inputContext);
                State.InputContext = null;
            }

            DecoderLock.Destroy();

            return 0;
        }

        /// <summary>
        /// Retrieves the next event to process after possibly refreshing video
        /// if there are no new events in the queue.
        /// Port of refresh_loop_wait_event
        /// </summary>
        /// <param name="vst">The VST.</param>
        /// <returns></returns>
        private MediaActionItem RefreshAndDequeueNextAction()
        {
            var remainingSeconds = 0.0d;

            while (ActionQueue.Count == 0)
            {
                if (remainingSeconds > 0.0)
                    Thread.Sleep(TimeSpan.FromSeconds(remainingSeconds));

                remainingSeconds = Constants.RefreshRateSeconds;
                if (State.IsPaused == false || State.IsVideoRefreshRequested)
                    remainingSeconds = RefreshVideoAndSubtitles(remainingSeconds);
            }

            return ActionQueue.Dequeue();
        }

        /// <summary>
        /// Process Action Queue Events continuously
        /// Port of event_loop
        /// </summary>
        private void ProcessActionQueueContinuously()
        {
            while (true)
            {
                var actionEvent = RefreshAndDequeueNextAction();
                switch (actionEvent.Action)
                {
                    case MediaAction.Quit:
                        Close();
                        break;
                    case MediaAction.ToggleFullScreen:
                        //toggle_full_screen(cur_stream);
                        State.IsVideoRefreshRequested = true;
                        break;
                    case MediaAction.TogglePause:
                        State.TogglePause();
                        break;
                    case MediaAction.ToggleMute:
                        State.ToggleMute();
                        break;
                    case MediaAction.VolumeUp:
                        State.UpdateVolume(1, Constants.SDL_VOLUME_STEP);
                        break;
                    case MediaAction.VolumeDown:
                        State.UpdateVolume(-1, Constants.SDL_VOLUME_STEP);
                        break;
                    case MediaAction.StepNextFrame:
                        State.StepToNextFrame();
                        break;
                    case MediaAction.CycleAudio:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_AUDIO);
                        break;
                    case MediaAction.CycleVideo:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_VIDEO);
                        break;
                    case MediaAction.CycleAll:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_VIDEO);
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_AUDIO);
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case MediaAction.CycleSubtitles:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case MediaAction.NextChapter:
                        if (State.InputContext->nb_chapters <= 1)
                        {
                            State.RequestSeekBy(600);
                            break;
                        }

                        State.SeekChapter(1);
                        break;
                    case MediaAction.PreviousChapter:
                        if (State.InputContext->nb_chapters <= 1)
                        {
                            State.RequestSeekBy(-600);
                            break;
                        }

                        State.SeekChapter(-1);
                        break;
                    case MediaAction.SeekLeft10:
                        State.RequestSeekBy(-10);
                        break;
                    case MediaAction.SeekRight10:
                        State.RequestSeekBy(10);
                        break;
                    case MediaAction.SeekLRight60:
                        State.RequestSeekBy(60);
                        break;
                    case MediaAction.SeekLeft60:
                        State.RequestSeekBy(-60);
                        break;
                    default:
                        break;
                }
            }
        }

        #endregion

        #region Event Raisers

        private void RaiseOnVideoDataAvailable(FrameHolder frame)
        {
            if (frame == null) return;
            OnVideoDataAvailable?.Invoke(this, new VideoDataEventArgs(frame));
        }

        private void RaiseOnSubtitleDataAvailable(FrameHolder frame)
        {
            if (frame == null) return;
            OnSubtitleDataAvailable?.Invoke(this, new SubtitleDataEventArgs(frame));
        }

        #endregion

    }
}