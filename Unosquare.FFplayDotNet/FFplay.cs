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

        #region State Variables

        internal long RednerAudioCallbackTimestamp;
        internal long LastVideoRefreshTimestamp;

        internal string[] wanted_stream_spec = new string[(int)AVMediaType.AVMEDIA_TYPE_NB];
        internal bool LogStatusMessages { get; set; } = true;

        #endregion

        #region Properties

        public int VideoScalerFlags { get; private set; } = ffmpeg.SWS_BICUBIC;
        public AVInputFormat* InputForcedFormat { get; private set; }

        public string MediaInputUrl { get; private set; }
        public string MediaTitle { get; private set; }

        internal SyncMode MediaSyncMode { get; set; } = SyncMode.Audio;
        public long MediaStartTimestamp { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public long MediaDuration { get; private set; } = ffmpeg.AV_NOPTS_VALUE;
        public bool? MediaSeekByBytes { get; private set; } = null;


        internal int default_width { get; set; } = 640;
        internal int default_height { get; set; } = 480;
        internal int screen_width { get; set; } = 0;
        internal int screen_height { get; set; } = 0;

        public bool IsAudioDisabled { get; private set; } = false;
        public bool IsVideoDisabled { get; private set; } = false;
        public bool IsSubtitleDisabled { get; private set; } = false;

        internal bool EnableFastDecoding { get; set; } = false;
        internal bool GeneratePts { get; set; } = false;
        internal bool EnableLowRes { get; set; } = false;

        internal bool autoexit { get; set; }
        internal int loop { get; set; } = 1;

        /// <summary>
        /// Enable dropping frames when cpu is too slow.
        /// Port of framedrop
        /// </summary>
        internal bool EnableFrameDrops { get; set; } = true;

        /// <summary>
        /// If set to > 0, don't limit the input buffer size (useful with realtime streams).
        /// Port of infinite_buffer
        /// </summary>
        internal int EnableInfiniteBuffer { get; set; } = -1;

        public string AudioCodecName { get; private set; }
        public string SubtitleCodecName { get; private set; }
        public string VideoCodecName { get; private set; }

        internal bool is_full_screen { get; set; }
        internal SDL_Window window { get; set; }
        internal SDL_Renderer renderer { get; set; }

        internal readonly MediaEventQueue EventQueue = new MediaEventQueue();
        internal Task MediaReadTask;

        internal readonly InterruptCallbackDelegate decode_interrupt_delegate = new InterruptCallbackDelegate(decode_interrupt_cb);
        internal readonly LockManagerCallbackDelegate lock_manager_delegate = new LockManagerCallbackDelegate(HandleLockOperation);

        #endregion

        public delegate int InterruptCallbackDelegate(void* opaque);
        public delegate int LockManagerCallbackDelegate(void** mutex, AVLockOp op);

        internal AVDictionary* FormatOptions = null;
        internal AVDictionary* CodecOptions = null;

        internal readonly MediaState State = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="FFplay"/> class.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <param name="fromatName">Name of the fromat. Leave null for automatic selection</param>
        public FFplay(string filename, string fromatName = null)
        {
            Helper.RegisterFFmpeg();

            #region init_opts

            var codecOpts = new AVDictionary();
            CodecOptions = &codecOpts;

            var formatOpts = new AVDictionary();
            FormatOptions = &formatOpts;

            #endregion

            #region main

            ffmpeg.avformat_network_init();

            if (string.IsNullOrWhiteSpace(fromatName) == false)
                InputForcedFormat = ffmpeg.av_find_input_format(fromatName);

            var lockManagerCallback = new av_lockmgr_register_cb_func
            {
                Pointer = Marshal.GetFunctionPointerForDelegate(lock_manager_delegate)
            };

            if (ffmpeg.av_lockmgr_register(lockManagerCallback) != 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not initialize lock manager!\n");
                do_exit();
            }

            State = new MediaState(this, filename, InputForcedFormat);
            MediaReadTask = Task.Run(() => { read_thread(); });
            event_loop();

            #endregion
        }

        /// <summary>
        /// Locking Handler for internal use of FFmpeg.
        /// Port of lockmgr
        /// </summary>
        /// <param name="mutexReference">The mutex reference.</param>
        /// <param name="lockOperation">The lock operation.</param>
        /// <returns></returns>
        private static int HandleLockOperation(void** mutexReference, AVLockOp lockOperation)
        {
            switch (lockOperation)
            {
                case AVLockOp.AV_LOCK_CREATE:
                    {
                        var mutex = new MonitorLock();
                        var mutexHandle = GCHandle.Alloc(mutex, GCHandleType.Pinned);
                        *mutexReference = (void*)mutexHandle.AddrOfPinnedObject();

                        if (*mutexReference == null)
                        {
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"SDL_CreateMutex(): {SDL_GetError()}\n");
                            return 1;
                        }
                        return 0;
                    }
                case AVLockOp.AV_LOCK_OBTAIN:
                    {
                        var mutex = GCHandle.FromIntPtr(new IntPtr(*mutexReference)).Target as MonitorLock;
                        mutex?.Lock();
                        return 0;
                    }
                case AVLockOp.AV_LOCK_RELEASE:
                    {
                        var mutex = GCHandle.FromIntPtr(new IntPtr(*mutexReference)).Target as MonitorLock;
                        mutex?.Unlock();
                        return 0;
                    }
                case AVLockOp.AV_LOCK_DESTROY:
                    {
                        var mutexHandle = GCHandle.FromIntPtr(new IntPtr(*mutexReference));
                        (mutexHandle.Target as MonitorLock)?.Destroy();

                        if (mutexHandle.IsAllocated)
                            mutexHandle.Free();

                        return 0;
                    }
            }

            return 1;
        }

        private int PollEvent() { return 0; }

        /// <summary>
        /// Retrieves the next event to process
        /// Port of refresh_loop_wait_event
        /// </summary>
        /// <param name="vst">The VST.</param>
        /// <returns></returns>
        private MediaEvent refresh_loop_wait_event()
        {
            double remainingSeconds = 0.0;
            //SDL_PumpEvents();
            while (PollEvent() == 0)
            {

                if (remainingSeconds > 0.0)
                    Thread.Sleep(TimeSpan.FromSeconds(remainingSeconds));

                remainingSeconds = Constants.RefreshRateSeconds;
                if (!State.IsPaused || State.IsForceRefreshRequested)
                    remainingSeconds = video_refresh(remainingSeconds);

                //SDL_PumpEvents();
            }

            // TODO: still missing some code here
            return new MediaEvent(this, MediaEventAction.AllocatePicture);
        }

        #region Methods

        public static void calculate_display_rect(SDL_Rect rect, int scr_xleft, int scr_ytop, int scr_width, int scr_height, int pic_width, int pic_height, AVRational pic_sar)
        {
            double aspect_ratio;
            int width, height, x, y;
            if (pic_sar.num == 0)
                aspect_ratio = 0;
            else
                aspect_ratio = Convert.ToSingle(ffmpeg.av_q2d(pic_sar));

            if (aspect_ratio <= 0.0)
                aspect_ratio = 1.0F;

            aspect_ratio *= (double)pic_width / (double)pic_height;
            height = scr_height;
            width = Convert.ToInt32(Math.Round(height * aspect_ratio)) & ~1;
            if (width > scr_width)
            {
                width = scr_width;
                height = Convert.ToInt32(Math.Round(width / aspect_ratio)) & ~1;
            }

            x = (scr_width - width) / 2;
            y = (scr_height - height) / 2;
            rect.x = scr_xleft + x;
            rect.y = scr_ytop + y;
            rect.w = Math.Max(width, 1);
            rect.h = Math.Max(height, 1);
        }

        /// <summary>
        /// Fills the Bitmap property of a Video frame.
        /// Port of upload_texture
        /// </summary>
        /// <param name="videoFrame">The video frame.</param>
        /// <returns></returns>
        public int FillBitmap(FrameHolder videoFrame)
        {
            var frame = videoFrame.DecodedFrame;

            if ((AVPixelFormat)frame->format == Constants.OutputPixelFormat)
            {
                // We don't need to do any colorspace transformation.
                videoFrame.FillBitmapDataFromDecodedFrame();
                return 0;
            }

            fixed (SwsContext** scalerReference = &State.VideoScaler)
            {
                // Retrieve a suitable scaler or create it on the fly
                *scalerReference = ffmpeg.sws_getCachedContext(*scalerReference,
                        frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height,
                        Constants.OutputPixelFormat, VideoScalerFlags, null, null, null);

                // Check for scaler availability
                if (*scalerReference == null)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                    return -1;
                }

                // Fill the buffer from scaler
                videoFrame.FillBitmapDataFromScaler(*scalerReference);
            }

            return 0;
        }

        private void do_exit()
        {
            if (State != null)
                State.CloseStream();

            // Do not process any more events
            EventQueue.Clear();

            if (renderer != null)
                SDL_DestroyRenderer(renderer);

            if (window != null)
                SDL_DestroyWindow(window);

            ffmpeg.av_lockmgr_register(null);

            // uninit_opts(); // NOTE: this is not rquired. Options are managed objects

            ffmpeg.avformat_network_deinit();

            SDL_Quit();
            ffmpeg.av_log(null, ffmpeg.AV_LOG_QUIET, "");
            // exit
        }

        private void set_default_window_size(int width, int height, AVRational sar)
        {
            var rect = new SDL_Rect();
            calculate_display_rect(rect, 0, 0, int.MaxValue, height, width, height, sar);
            default_width = rect.w;
            default_height = rect.h;
        }

        private int video_open(FrameHolder vp)
        {
            int w, h;
            if (vp != null && vp.PictureWidth != 0)
                set_default_window_size(vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

            if (screen_width != 0)
            {
                w = screen_width;
                h = screen_height;
            }
            else
            {
                w = default_width;
                h = default_height;
            }

            if (window != null)
            {
                int flags = SDL_WINDOW_SHOWN | SDL_WINDOW_RESIZABLE;

                if (is_full_screen)
                    flags |= SDL_WINDOW_FULLSCREEN_DESKTOP;

                window = SDL_CreateWindow(string.Empty, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, w, h, flags);
                SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "linear");
                if (window != null)
                {
                    var info = new SDL_RendererInfo();
                    renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
                    if (renderer != null)
                    {
                        if (SDL_GetRendererInfo(renderer, info) == 0)
                            ffmpeg.av_log(null, ffmpeg.AV_LOG_VERBOSE, $"Initialized renderer.\n");
                    }
                }
            }
            else
            {
                SDL_SetWindowSize(window, w, h);
            }

            if (window == null || renderer == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "SDL: could not set video mode - exiting\n");
                do_exit();
            }

            State.PictureWidth = w;
            State.PictureHeight = h;
            return 0;
        }

        public int audio_open(long wantedChannelLayout, int wantedChannelCount, int wantedSampleRate, AudioParams audioHardware)
        {
            var wantedAudioSpec = new SDL_AudioSpec();
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
            wantedAudioSpec.callback = new SDL_AudioCallback(sdl_audio_callback);
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


        private void video_display()
        {
            if (window == null)
                video_open(null);

            SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            SDL_RenderClear(renderer);

            if (State.VideoStream != null)
                State.video_image_display();

            SDL_RenderPresent(renderer);
        }

        /// <summary>
        /// Allocs the picture.
        /// Port of alloc_picture
        /// </summary>
        private void alloc_picture()
        {
            var videoFrame = State.VideoQueue.Frames[State.VideoQueue.WriteIndex];
            video_open(videoFrame);

            State.VideoQueue.SignalDoneWriting(() =>
            {
                videoFrame.IsAllocated = true;
            });
        }

        private double video_refresh(double remaining_time)
        {
            //double currentTimeSeconds;
            var currentSubtitleFrame = new FrameHolder();
            var nextSubtitleFrame = new FrameHolder();

            if (!State.IsPaused && State.MasterSyncMode == SyncMode.External && State.IsMediaRealtime)
                State.AdjustExternalClockSpeedRatio();

            if (State.VideoStream != null)
            {
                retry:
                if (State.VideoQueue.PendingCount == 0)
                {
                    // nothing to do, no picture to display in the queue
                }
                else
                {
                    var lastVideoFrame = State.VideoQueue.Last;
                    var currentVideoFrame = State.VideoQueue.Current;

                    if (currentVideoFrame.Serial != State.VideoPackets.Serial)
                    {
                        State.VideoQueue.QueueNextRead();
                        goto retry;
                    }
                    if (lastVideoFrame.Serial != currentVideoFrame.Serial)
                        State.VideoFrameTimeSeconds = (double)ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE;

                    if (State.IsPaused)
                        goto display;

                    var lastFrameDuration = State.ComputeVideoFrameDurationSeconds(lastVideoFrame, currentVideoFrame);
                    var delaySeconds = State.ComputeVideoClockDelay(lastFrameDuration);
                    var currentTimeSeconds = ffmpeg.av_gettime_relative() / (double)ffmpeg.AV_TIME_BASE;

                    if (currentTimeSeconds < State.VideoFrameTimeSeconds + delaySeconds)
                    {
                        remaining_time = Math.Min(State.VideoFrameTimeSeconds + delaySeconds - currentTimeSeconds, remaining_time);
                        goto display;
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
                        var nextVideoFrame = State.VideoQueue.Next;
                        var durationSeconds = State.ComputeVideoFrameDurationSeconds(currentVideoFrame, nextVideoFrame);

                        if (!State.IsFrameStepping
                            && EnableFrameDrops
                            && currentTimeSeconds > State.VideoFrameTimeSeconds + durationSeconds)
                        {
                            State.frame_drops_late++;
                            State.VideoQueue.QueueNextRead();
                            goto retry;
                        }
                    }

                    if (State.SubtitleStream != null)
                    {
                        while (State.SubtitleQueue.PendingCount > 0)
                        {
                            currentSubtitleFrame = State.SubtitleQueue.Current;
                            if (State.SubtitleQueue.PendingCount > 1)
                                nextSubtitleFrame = State.SubtitleQueue.Next;
                            else
                                nextSubtitleFrame = null;

                            if (currentSubtitleFrame.Serial != State.SubtitlePackets.Serial
                                    || (State.VideoClock.PtsSeconds > (currentSubtitleFrame.PtsSeconds + ((float)currentSubtitleFrame.Subtitle.end_display_time / 1000)))
                                    || (nextSubtitleFrame != null && State.VideoClock.PtsSeconds > (nextSubtitleFrame.PtsSeconds + ((float)nextSubtitleFrame.Subtitle.start_display_time / 1000))))
                            {
                                if (currentSubtitleFrame.IsUploaded)
                                {
                                    for (var i = 0; i < currentSubtitleFrame.Subtitle.num_rects; i++)
                                    {
                                        var sub_rect = currentSubtitleFrame.Subtitle.rects[i];
                                        byte** pixels = null;

                                        var pitch = 0;

                                        if (SDL_LockTexture(State.subtitleTexture, sub_rect, pixels, &pitch) == 0)
                                        {
                                            for (var j = 0; j < sub_rect->h; j++, pixels += pitch)
                                                Native.memset(*pixels, 0, sub_rect->w << 2);

                                            SDL_UnlockTexture(State.subtitleTexture);
                                        }
                                    }
                                }

                                State.SubtitleQueue.QueueNextRead();
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    State.VideoQueue.QueueNextRead();
                    State.IsForceRefreshRequested = true;
                    if (State.IsFrameStepping && !State.IsPaused)
                        State.StreamTogglePause();
                }
                display:
                if (State.IsForceRefreshRequested && State.VideoQueue.ReadIndexShown != 0)
                    video_display();
            }

            State.IsForceRefreshRequested = false;
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
                       $"{State.MasterClockPositionSeconds} {mode}:{clockSkew} fd={State.frame_drops_early + State.frame_drops_late} aq={audioQueueSize / 1024}KB " +
                       $"vq={videoQueueSize / 1024}KB sq={subtitleQueueSize}dB f={faultyDts} / {faultyPts}\r");
            }

            LastVideoRefreshTimestamp = currentTimestamp;
            return remaining_time;
        }

        private void sdl_audio_callback(MediaState vst, byte* stream, int length)
        {
            RednerAudioCallbackTimestamp = ffmpeg.av_gettime_relative();

            while (length > 0)
            {
                if (vst.RenderAudioBufferIndex >= vst.RenderAudioBufferLength)
                {
                    var bufferLength = vst.DecodeAudioFrame();
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

        static int decode_interrupt_cb(void* opaque)
        {
            var vst = GCHandle.FromIntPtr(new IntPtr(opaque)).Target as MediaState;
            return vst.IsAbortRequested ? 1 : 0;
        }


        public int EnqueuePicture(AVFrame* sourceFrame, double pts, double duration)
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

            if (videoFrame.Bitmap == null || !videoFrame.IsAllocated ||
                videoFrame.PictureWidth != sourceFrame->width ||
                videoFrame.PictureHeight != sourceFrame->height ||
                videoFrame.format != sourceFrame->format)
            {

                videoFrame.IsAllocated = false;
                videoFrame.PictureWidth = sourceFrame->width;
                videoFrame.PictureHeight = sourceFrame->height;
                videoFrame.format = sourceFrame->format;

                EventQueue.PushEvent(this, MediaEventAction.AllocatePicture);

                try
                {
                    State.VideoQueue.SyncLock.Lock();
                    while (!videoFrame.IsAllocated && !State.VideoPackets.IsAborted)
                        State.VideoQueue.IsDoneWriting.Wait(State.VideoQueue.SyncLock);

                    if (State.VideoPackets.IsAborted && EventQueue.DequeueEvents(MediaEventAction.AllocatePicture).Length != 1)
                    {
                        while (!videoFrame.IsAllocated && !State.IsAbortRequested)
                            State.VideoQueue.IsDoneWriting.Wait(State.VideoQueue.SyncLock);
                    }
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

        #region Decoding Tasks: Frames Queues for audio, video, and subtitles

        /// <summary>
        /// Continuously decodes video frames from the video frame queue
        /// using the video decoder.
        /// Port of video_thread
        /// </summary>
        /// <param name="mediaState">The media state object</param>
        internal static void DecodeVideoQueue(MediaState mediaState)
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

                result = mediaState.Player.EnqueuePicture(decodedFrame, framePts, frameDuration);
                ffmpeg.av_frame_unref(decodedFrame);

                if (result < 0) break;
            }

            ffmpeg.av_frame_free(&decodedFrame);
        }

        /// <summary>
        /// Continuously decodes video frames from the audio frame queue
        /// using the audio decoder.
        /// Port of audio_thread
        /// </summary>
        /// <param name="mediaState">The media state object.</param>
        internal static void DecodeAudioQueue(MediaState mediaState)
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
        internal static void DecodeSubtitlesQueue(MediaState mediaState)
        {
            var hasDecoded = 0;

            while (true)
            {
                var decodedFrame = mediaState.SubtitleQueue.PeekWritableFrame();
                decodedFrame.MediaType = AVMediaType.AVMEDIA_TYPE_SUBTITLE;

                if (decodedFrame == null)
                    return;

                fixed (AVSubtitle* subtitlePtr = &decodedFrame.Subtitle)
                    hasDecoded = mediaState.SubtitleDecoder.Decode(subtitlePtr);

                if (hasDecoded < 0)
                    break;

                if (hasDecoded != 0 && decodedFrame.Subtitle.format == 0)
                {
                    decodedFrame.PtsSeconds = (decodedFrame.Subtitle.pts != ffmpeg.AV_NOPTS_VALUE) ?
                        decodedFrame.Subtitle.pts / (double)ffmpeg.AV_TIME_BASE : 0;
                    decodedFrame.Serial = mediaState.SubtitleDecoder.PacketSerial;
                    decodedFrame.PictureWidth = mediaState.SubtitleDecoder.Codec->width;
                    decodedFrame.PictureHeight = mediaState.SubtitleDecoder.Codec->height;
                    decodedFrame.IsUploaded = false;

                    /* now we can update the picture count */
                    mediaState.SubtitleQueue.QueueNextWrite();
                }
                else if (hasDecoded != 0)
                {
                    fixed (AVSubtitle* subtitlePtr = &decodedFrame.Subtitle)
                        ffmpeg.avsubtitle_free(subtitlePtr);
                }
            }
        }

        #endregion

        public int read_thread()
        {
            AVFormatContext* inputContext = null;
            int err, i, ret;
            var streamIndexes = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            var pkt1 = new AVPacket();
            AVPacket* pkt = &pkt1;

            AVDictionaryEntry* t;
            AVDictionary** opts;

            var inputStreamCount = 0;
            var isPacketInPlayRange = false;

            var scan_all_pmts_set = false;
            long pkt_ts;

            var DecoderLock = new MonitorLock();

            Native.memset(streamIndexes, -1, streamIndexes.Length);
            State.LastVideoStreamIndex = State.VideoStreamIndex = -1;
            State.LastAudioStreamIndex = State.AudioStreamIndex = -1;
            State.LastSubtitleStreamIndex = State.SubtitleStreamIndex = -1;
            State.IsAtEndOfFile = false;
            inputContext = ffmpeg.avformat_alloc_context();
            if (inputContext == null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Could not allocate context.\n");
                ret = ffmpeg.AVERROR_ENOMEM;
                goto fail;
            }

            inputContext->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(decode_interrupt_delegate) };
            inputContext->interrupt_callback.opaque = (void*)State.Handle.AddrOfPinnedObject();

            fixed (AVDictionary** formatOptsPtr = &FormatOptions)
            {
                if (ffmpeg.av_dict_get(FormatOptions, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE) == null)
                {
                    ffmpeg.av_dict_set(formatOptsPtr, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
                    scan_all_pmts_set = true;
                }

                err = ffmpeg.avformat_open_input(&inputContext, State.MediaUrl, State.InputFormat, formatOptsPtr);
                if (err < 0)
                {
                    Debug.WriteLine($"Error in read_thread. File '{State.MediaUrl}'. {err}");
                    ret = -1;
                    goto fail;
                }

                if (scan_all_pmts_set)
                    ffmpeg.av_dict_set(formatOptsPtr, "scan_all_pmts", null, ffmpeg.AV_DICT_MATCH_CASE);

                t = ffmpeg.av_dict_get(FormatOptions, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX);

            }

            if (t != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Native.BytePtrToString(t->key)} not found.\n");
                ret = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            State.InputContext = inputContext;
            if (GeneratePts)
                inputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;

            ffmpeg.av_format_inject_global_side_data(inputContext);

            opts = Helper.RetrieveStreamOptions(inputContext, CodecOptions);
            inputStreamCount = Convert.ToInt32(inputContext->nb_streams);
            err = ffmpeg.avformat_find_stream_info(inputContext, opts);
            for (i = 0; i < inputStreamCount; i++)
                ffmpeg.av_dict_free(&opts[i]);

            ffmpeg.av_freep(&opts);

            if (err < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{State.MediaUrl}: could not find codec parameters\n");
                ret = -1;
                goto fail;
            }

            if (inputContext->pb != null)
                inputContext->pb->eof_reached = 0; // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end

            var formatName = Native.BytePtrToString(inputContext->iformat->name);
            var isDiscontinuous = (inputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;

            // seek by byes only for continuous ogg vorbis
            if (MediaSeekByBytes.HasValue == false)
                MediaSeekByBytes = isDiscontinuous && formatName.Equals("ogg") == false;

            State.MaximumFrameDuration = isDiscontinuous ? 10.0 : 3600.0;

            t = ffmpeg.av_dict_get(inputContext->metadata, "title", null, 0);
            if (t != null)
                MediaTitle = Native.BytePtrToString(t->value);


            if (MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE)
            {
                long timestamp;
                timestamp = MediaStartTimestamp;
                if (inputContext->start_time != ffmpeg.AV_NOPTS_VALUE)
                    timestamp += inputContext->start_time;

                ret = ffmpeg.avformat_seek_file(inputContext, -1, long.MinValue, timestamp, long.MaxValue, 0);

                if (ret < 0)
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"{State.MediaUrl}: could not seek to position {((double)timestamp / ffmpeg.AV_TIME_BASE)}\n");
            }

            if (LogStatusMessages)
                ffmpeg.av_dump_format(inputContext, 0, State.MediaUrl, 0);

            for (i = 0; i < inputContext->nb_streams; i++)
            {
                var st = inputContext->streams[i];
                var type = st->codecpar->codec_type;
                st->discard = AVDiscard.AVDISCARD_ALL;

                if (type >= 0 && string.IsNullOrWhiteSpace(wanted_stream_spec[(int)type]) == false && streamIndexes[(int)type] == -1)
                    if (ffmpeg.avformat_match_stream_specifier(inputContext, st, wanted_stream_spec[(int)type]) > 0)
                        streamIndexes[(int)type] = i;
            }

            for (i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
            {
                if (string.IsNullOrWhiteSpace(wanted_stream_spec[i]) == false && streamIndexes[i] == -1)
                {
                    ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                        $"Stream specifier {wanted_stream_spec[i]} does not match any {ffmpeg.av_get_media_type_string((AVMediaType)i)} stream\n");

                    streamIndexes[i] = int.MaxValue;
                }
            }

            if (!IsVideoDisabled)
                streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                    ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);

            if (!IsAudioDisabled)
                streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                    ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                        null, 0);

            if (!IsVideoDisabled && !IsSubtitleDisabled)
                streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                    ffmpeg.av_find_best_stream(inputContext, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                        (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                         streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                        null, 0);

            //vst.show_mode = show_mode;
            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                var st = inputContext->streams[streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]];
                var codecpar = st->codecpar;
                var sar = ffmpeg.av_guess_sample_aspect_ratio(inputContext, st, null);
                if (codecpar->width != 0)
                    set_default_window_size(codecpar->width, codecpar->height, sar);
            }
            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
            {
                State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]);
            }

            ret = -1;
            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
            {
                ret = State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]);
            }

            //if (vst.show_mode == ShowMode.SHOW_MODE_NONE)
            //    vst.show_mode = ret >= 0 ? ShowMode.SHOW_MODE_VIDEO : ShowMode.SHOW_MODE_NONE;

            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
            {
                State.OpenStreamComponent(streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE]);
            }

            if (State.VideoStreamIndex < 0 && State.AudioStreamIndex < 0)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, $"Failed to open file '{State.MediaUrl}' or configure filtergraph\n");
                ret = -1;
                goto fail;
            }

            if (EnableInfiniteBuffer < 0 && State.IsMediaRealtime)
                EnableInfiniteBuffer = 1;

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
                    (formatName.Equals("rtsp") || (inputContext->pb != null && MediaInputUrl.StartsWith("mmsh:"))))
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

                    ret = ffmpeg.avformat_seek_file(State.InputContext, -1, seekMin, seekTarget, seekMax, State.SeekModeFlags);

                    if (ret < 0)
                    {
                        ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR,
                               $"{Encoding.GetEncoding(0).GetString(State.InputContext->filename)}: error while seeking\n");
                    }
                    else
                    {
                        if (State.AudioStreamIndex >= 0)
                        {
                            State.AudioPackets.Clear();
                            State.AudioPackets.Enqueue(PacketQueue.FlushPacket);
                        }

                        if (State.SubtitleStreamIndex >= 0)
                        {
                            State.SubtitlePackets.Clear();
                            State.SubtitlePackets.Enqueue(PacketQueue.FlushPacket);
                        }

                        if (State.VideoStreamIndex >= 0)
                        {
                            State.VideoPackets.Clear();
                            State.VideoPackets.Enqueue(PacketQueue.FlushPacket);
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
                        var copy = new AVPacket();
                        if ((ret = ffmpeg.av_copy_packet(&copy, &State.VideoStream->attached_pic)) < 0)
                            goto fail;

                        State.VideoPackets.Enqueue(&copy);
                        State.VideoPackets.EnqueueEmptyPacket(State.VideoStreamIndex);
                    }

                    State.EnqueuePacketAttachments = false;
                }

                if (EnableInfiniteBuffer < 1 &&
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
                    if (loop != 1 && (loop == 0 || --loop == 0))
                    {
                        State.RequestSeekTo(MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0, 0, false);
                    }
                    else if (autoexit)
                    {
                        ret = ffmpeg.AVERROR_EOF;
                        goto fail;
                    }
                }
                ret = ffmpeg.av_read_frame(inputContext, pkt);
                if (ret < 0)
                {
                    if ((ret == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(inputContext->pb) != 0) && !State.IsAtEndOfFile)
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

                var stream_start_time = inputContext->streams[pkt->stream_index]->start_time;
                pkt_ts = pkt->pts == ffmpeg.AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                isPacketInPlayRange = MediaDuration == ffmpeg.AV_NOPTS_VALUE ||
                        (pkt_ts - (stream_start_time != ffmpeg.AV_NOPTS_VALUE ? stream_start_time : 0)) *
                        ffmpeg.av_q2d(inputContext->streams[pkt->stream_index]->time_base) -
                        (double)(MediaStartTimestamp != ffmpeg.AV_NOPTS_VALUE ? MediaStartTimestamp : 0) / ffmpeg.AV_TIME_BASE
                        <= ((double)MediaDuration / ffmpeg.AV_TIME_BASE);

                if (pkt->stream_index == State.AudioStreamIndex && isPacketInPlayRange)
                {
                    State.AudioPackets.Enqueue(pkt);
                }
                else if (pkt->stream_index == State.VideoStreamIndex && isPacketInPlayRange
                         && (State.VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    State.VideoPackets.Enqueue(pkt);
                }
                else if (pkt->stream_index == State.SubtitleStreamIndex && isPacketInPlayRange)
                {
                    State.SubtitlePackets.Enqueue(pkt);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }

            ret = 0;
            fail:
            if (inputContext != null && State.InputContext == null)
                ffmpeg.avformat_close_input(&inputContext);

            if (ret != 0)
            {
                EventQueue.PushEvent(this, MediaEventAction.Quit);
            }

            DecoderLock.Destroy();
            return 0;
        }

        private void event_loop()
        {
            double incr;
            double pos;

            while (true)
            {
                var actionEvent = refresh_loop_wait_event();
                switch (actionEvent.Action)
                {
                    case MediaEventAction.Quit:
                        do_exit();
                        break;
                    case MediaEventAction.ToggleFullScreen:
                        //toggle_full_screen(cur_stream);
                        State.IsForceRefreshRequested = true;
                        break;
                    case MediaEventAction.TogglePause:
                        State.TogglePause();
                        break;
                    case MediaEventAction.ToggleMute:
                        State.ToggleMute();
                        break;
                    case MediaEventAction.VolumeUp:
                        State.UpdateVolume(1, Constants.SDL_VOLUME_STEP);
                        break;
                    case MediaEventAction.VolumeDown:
                        State.UpdateVolume(-1, Constants.SDL_VOLUME_STEP);
                        break;
                    case MediaEventAction.StepNextFrame:
                        State.StepToNextFrame();
                        break;
                    case MediaEventAction.CycleAudio:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_AUDIO);
                        break;
                    case MediaEventAction.CycleVideo:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_VIDEO);
                        break;
                    case MediaEventAction.CycleAll:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_VIDEO);
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_AUDIO);
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case MediaEventAction.CycleSubtitles:
                        State.CycleStreamChannel(AVMediaType.AVMEDIA_TYPE_SUBTITLE);
                        break;
                    case MediaEventAction.NextChapter:
                        if (State.InputContext->nb_chapters <= 1)
                        {
                            incr = 600.0;
                            goto do_seek;
                        }

                        State.SeekChapter(1);
                        break;
                    case MediaEventAction.PreviousChapter:
                        if (State.InputContext->nb_chapters <= 1)
                        {
                            incr = -600.0;
                            goto do_seek;
                        }

                        State.SeekChapter(-1);
                        break;
                    case MediaEventAction.SeekLeft10:
                        incr = -10.0;
                        goto do_seek;
                    case MediaEventAction.SeekRight10:
                        incr = 10.0;
                        goto do_seek;
                    case MediaEventAction.SeekLRight60:
                        incr = 60.0;
                        goto do_seek;
                    case MediaEventAction.SeekLeft60:
                        incr = -60.0;
                        do_seek:
                        if (MediaSeekByBytes.HasValue && MediaSeekByBytes.Value == true)
                        {
                            pos = -1;
                            if (pos < 0 && State.VideoStreamIndex >= 0)
                                pos = State.VideoQueue.StreamPosition;

                            if (pos < 0 && State.AudioStreamIndex >= 0)
                                pos = State.AudioQueue.StreamPosition;

                            if (pos < 0)
                                pos = State.InputContext->pb->pos; // TODO: ffmpeg.avio_tell(cur_stream.ic->pb); avio_tell not available here

                            if (State.InputContext->bit_rate != 0)
                                incr *= State.InputContext->bit_rate / 8.0;
                            else
                                incr *= 180000.0;

                            pos += incr;
                            State.RequestSeekTo((long)pos, (long)incr, true);
                        }
                        else
                        {
                            pos = State.MasterClockPositionSeconds;
                            if (double.IsNaN(pos))
                                pos = (double)State.SeekTargetPosition / ffmpeg.AV_TIME_BASE;

                            pos += incr;

                            if (State.InputContext->start_time != ffmpeg.AV_NOPTS_VALUE && pos < State.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE)
                                pos = State.InputContext->start_time / (double)ffmpeg.AV_TIME_BASE;

                            State.RequestSeekTo((long)(pos * ffmpeg.AV_TIME_BASE), (long)(incr * ffmpeg.AV_TIME_BASE), false);
                        }
                        break;
                    case MediaEventAction.AllocatePicture:
                        alloc_picture();
                        break;
                    default:
                        break;
                }
            }
        }

        #endregion

    }
}