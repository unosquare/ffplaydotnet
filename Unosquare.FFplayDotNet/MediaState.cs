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

    public unsafe class MediaState
    {
        internal readonly GCHandle Handle;

        internal AVInputFormat* InputFormat;
        internal AVFormatContext* InputContext;

        internal AVStream* AudioStream;
        internal AVStream* SubtitleStream;
        internal AVStream* VideoStream;

        internal SwrContext* AudioScaler;
        internal SwsContext* VideoScaler;
        internal SwsContext* SubtitleScaler;

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

        public int frame_drops_early;
        public int frame_drops_late;
        public double frame_timer;
        public double frame_last_returned_time;
        public double frame_last_filter_delay;

        public int xleft;
        public int ytop;
        public int xpos;


        public LockCondition IsFrameDecoded;
        public SDL_Texture vis_texture;
        public SDL_Texture sub_texture;

        internal FFplay Player { get; private set; }


        public bool? IsPtsReorderingEnabled { get; private set; } = null;

        public bool IsAbortRequested { get; internal set; }
        public bool IsForceRefreshRequested { get; internal set; }
        public bool IsPaused { get; set; }

        public bool IsSeekRequested { get; internal set; }
        public int SeekModeFlags { get; private set; }
        public long SeekTargetPosition { get; private set; }
        public long SeekTargetRange { get; private set; }

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

        public double MasterClockPosition
        {
            get
            {
                double val;
                switch (MasterSyncMode)
                {
                    case SyncMode.Video:
                        val = VideoClock.Position;
                        break;
                    case SyncMode.Audio:
                        val = AudioClock.Position;
                        break;
                    default:
                        val = ExternalClock.Position;
                        break;
                }
                return val;

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
        /// Gets the maximum duration of the frame.
        /// above this, we consider the jump a timestamp discontinuity
        /// </summary>
        public double MaximumFrameDuration { get; internal set; }

        public bool IsAtEndOfFile { get; internal set; }
        public string MediaUrl { get; internal set; }
        public int PictureWidth { get; internal set; }
        public int PictureHeight { get; internal set; }
        public bool IsFrameStepping { get; internal set; }


        internal MediaState(FFplay player, string filename, AVInputFormat* iformat)
        {
            Handle = GCHandle.Alloc(this, GCHandleType.Pinned);

            Player = player;
            MediaUrl = filename;
            InputFormat = iformat;
            ytop = 0;
            xleft = 0;

            VideoQueue = new FrameQueue(VideoPackets, Constants.VideoQueueSize, true);
            SubtitleQueue = new FrameQueue(SubtitlePackets, Constants.SubtitleQueueSize, false);
            AudioQueue = new FrameQueue(AudioPackets, Constants.SampleQueueSize, true);

            IsFrameDecoded = new LockCondition();

            VideoClock = new Clock(() => { return new int?(VideoPackets.Serial); });
            AudioClock = new Clock(() => { return new int?(AudioPackets.Serial); });
            ExternalClock = new Clock(() => { return new int?(ExternalClock.PacketSerial); });

            DecodedAudioClockSerial = -1;
            AudioVolume = SDL_MIX_MAXVOLUME;
            IsAudioMuted = false;
            MediaSyncMode = MediaSyncMode;

        }

        public void AdjustExternalClockSpeedRatio()
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

        public void RequestSeekTo(long pos, long rel, bool seekByBytes)
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

        public void SeekChapter(int increment)
        {
            long pos = Convert.ToInt64(MasterClockPosition * ffmpeg.AV_TIME_BASE);
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

        public void CycleStreamChannel(AVMediaType mediaType)
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
                        goto the_end;
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
                    switch (mediaType)
                    {
                        case AVMediaType.AVMEDIA_TYPE_AUDIO:
                            if (st->codecpar->sample_rate != 0 &&
                                st->codecpar->channels != 0)
                                goto the_end;
                            break;
                        case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                            goto the_end;
                        default:
                            break;
                    }
                }
            }

            the_end:

            if (program != null && targetIndex != -1)
                targetIndex = (int)program->stream_index[targetIndex];

            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, $"Switch {ffmpeg.av_get_media_type_string(mediaType)} stream from #{prevIndex} to #{targetIndex}\n");

            CloseStreamComponent(prevIndex);
            OpenStreamComponent(targetIndex);
        }

        public void CloseStreamComponent(int streamIndex)
        {
            var ic = InputContext;

            if (streamIndex < 0 || streamIndex >= ic->nb_streams)
                return;

            var codecParams = ic->streams[streamIndex]->codecpar;
            switch (codecParams->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    AudioDecoder.Abort(AudioQueue);
                    SDL_CloseAudio();
                    AudioDecoder.ReleaseUnmanaged();
                    fixed (SwrContext** vst_swr_ctx = &AudioScaler)
                    {
                        ffmpeg.swr_free(vst_swr_ctx);
                    }

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

            ic->streams[streamIndex]->discard = AVDiscard.AVDISCARD_ALL;

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

        public int OpenStreamComponent(int streamIndex)
        {
            var ic = InputContext;
            string forcedCodecName = null;
            AVDictionaryEntry* kvp = null;

            int sample_rate = 0;
            int channelCount = 0;
            long channelLayout = 0;
            int result = 0;

            int lowResIndex = Convert.ToInt32(Player.EnableLowRes);
            if (streamIndex < 0 || streamIndex >= ic->nb_streams)
                return -1;

            var codecContext = ffmpeg.avcodec_alloc_context3(null);
            if (codecContext == null)
                return ffmpeg.AVERROR_ENOMEM;

            result = ffmpeg.avcodec_parameters_to_context(codecContext, ic->streams[streamIndex]->codecpar);
            if (result < 0)
                goto fail;

            ffmpeg.av_codec_set_pkt_timebase(codecContext, ic->streams[streamIndex]->time_base);
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
                goto fail;
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

            if (Player.EnableFastDecoding)
                codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if ((decoder->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
                codecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

            var opts = FFplay.filter_codec_opts(Player.CodecOptions, codecContext->codec_id, ic, ic->streams[streamIndex], decoder);

            if (ffmpeg.av_dict_get(opts, "threads", null, 0) == null)
                ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            if (lowResIndex != 0)
                ffmpeg.av_dict_set_int(&opts, "lowres", lowResIndex, 0);

            if (codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                ffmpeg.av_dict_set(&opts, "refcounted_frames", "1", 0);

            if ((result = ffmpeg.avcodec_open2(codecContext, decoder, &opts)) < 0)
            {
                goto fail;
            }

            if ((kvp = ffmpeg.av_dict_get(opts, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, $"Option {Native.BytePtrToString(kvp->key)} not found.\n");
                result = ffmpeg.AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }

            IsAtEndOfFile = false;
            ic->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;

            switch (codecContext->codec_type)
            {
                case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    if ((result = Player.audio_open(this, channelLayout, channelCount, sample_rate, AudioOutputParams)) < 0)
                        goto fail;

                    AudioHardwareBufferSize = result;
                    AudioOutputParams.CopyTo(AudioInputParams);
                    RenderAudioBufferLength = 0;
                    RenderAudioBufferIndex = 0;
                    AudioSkewCoefficient = Math.Exp(Math.Log(0.01) / Constants.AudioSkewMinSamples);
                    AudioSkewAvgCount = 0;
                    AudioSkewThreshold = (double)(AudioHardwareBufferSize) / AudioOutputParams.BytesPerSecond;
                    AudioStreamIndex = streamIndex;
                    AudioStream = ic->streams[streamIndex];

                    AudioDecoder = new Decoder(this, codecContext, AudioPackets, IsFrameDecoded);

                    if ((InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
                        InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
                    {
                        AudioDecoder.StartPts = AudioStream->start_time;
                        AudioDecoder.StartPtsTimebase = AudioStream->time_base;
                    }

                    AudioDecoder.Start(FFplay.DecodeAudioQueue);
                    SDL_PauseAudio(0);
                    break;

                case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    VideoStreamIndex = streamIndex;
                    VideoStream = ic->streams[streamIndex];
                    VideoDecoder = new Decoder(this, codecContext, VideoPackets, IsFrameDecoded);
                    result = VideoDecoder.Start(FFplay.DecodeVideoQueue);
                    EnqueuePacketAttachments = true;
                    break;

                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    SubtitleStreamIndex = streamIndex;
                    SubtitleStream = ic->streams[streamIndex];
                    SubtitleDecoder = new Decoder(this, codecContext, SubtitlePackets, IsFrameDecoded);
                    result = SubtitleDecoder.Start(FFplay.DecodeSubtitlesQueue);
                    break;

                default:
                    break;
            }

            goto final;
            fail:
            ffmpeg.avcodec_free_context(&codecContext);
            final:
            ffmpeg.av_dict_free(&opts);
            return result;
        }

        /// <summary>
        /// Closes the media along with all of its streams.
        /// Port of stream_close
        /// </summary>
        public void CloseStream()
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
            {
                ffmpeg.avformat_close_input(vstic);
            }

            VideoPackets.Clear();
            AudioPackets.Clear();
            SubtitlePackets.Clear();
            VideoQueue.Clear();
            AudioQueue.Clear();
            SubtitleQueue.Clear();
            IsFrameDecoded.Dispose();
            
            ffmpeg.sws_freeContext(VideoScaler);
            ffmpeg.sws_freeContext(SubtitleScaler);

            if (vis_texture != null)
                SDL_DestroyTexture(vis_texture);
            if (sub_texture != null)
                SDL_DestroyTexture(sub_texture);
        }

        /// <summary>
        /// Decodes the Audio frames in the audio frame queue until
        /// the packet serial matches the audio frame serial. It then resamples the
        /// frame.
        /// </summary>
        /// <returns></returns>
        public int DecodeAudioFrame()
        {
            // TODO: Move this method to decoder if possible

            int decodedDataSize;
            int resampledDataSize;
            long decodedFrameChannelLayout;

            int wantedSampleCount;
            FrameHolder audioFrame = null;

            if (IsPaused) return -1;

            do
            {
                while (AudioQueue.PendingCount == 0)
                {
                    if ((ffmpeg.av_gettime_relative() - Player.RednerAudioCallbackTimestamp) > 1000000L * AudioHardwareBufferSize / AudioOutputParams.BytesPerSecond / 2)
                        return -1;

                    Thread.Sleep(1); //ffmpeg.av_usleep(1000);
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
                fixed (SwrContext** vst_swr_ctx = &AudioScaler)
                    ffmpeg.swr_free(vst_swr_ctx);

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

                    fixed (SwrContext** audoScalerPointer = &AudioScaler)
                        ffmpeg.swr_free(audoScalerPointer);

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
                        fixed (SwrContext** vst_swr_ctx = &AudioScaler)
                            ffmpeg.swr_free(vst_swr_ctx);
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
            if (!double.IsNaN(audioFrame.Pts))
                DecodedAudioClockPosition = audioFrame.Pts + (double)audioFrame.DecodedFrame->nb_samples / audioFrame.DecodedFrame->sample_rate;
            else
                DecodedAudioClockPosition = double.NaN;

            DecodedAudioClockSerial = audioFrame.Serial;

            return resampledDataSize;
        }

        internal double ComputeVideoClockDelay(double delay)
        {
            var skew = 0d;

            if (MasterSyncMode != SyncMode.Video)
            {
                skew = VideoClock.Position - MasterClockPosition;
                var syncThreshold = Math.Max(
                    Constants.AvSyncThresholdMin,
                    Math.Min(Constants.AvSyncThresholdMax, delay));

                if (!double.IsNaN(skew) && Math.Abs(skew) < MaximumFrameDuration)
                {
                    if (skew <= -syncThreshold)
                        delay = Math.Max(0, delay + skew);
                    else if (skew >= syncThreshold && delay > Constants.AvSuncFrameDupThreshold)
                        delay = delay + skew;
                    else if (skew >= syncThreshold)
                        delay = 2 * delay;
                }
            }

            ffmpeg.av_log(null, ffmpeg.AV_LOG_TRACE, $"video: delay={delay} A-V={-skew}\n");
            return delay;
        }

        internal double ComputeVideoFrameDuration(FrameHolder videoFrame, FrameHolder nextVideoFrame)
        {
            if (videoFrame.Serial == nextVideoFrame.Serial)
            {
                var duration = nextVideoFrame.Pts - videoFrame.Pts;
                if (double.IsNaN(duration) || duration <= 0 || duration > MaximumFrameDuration)
                    return videoFrame.Duration;
                else
                    return duration;
            }

            return 0.0;
        }

        public void video_image_display()
        {
            var vp = new FrameHolder();
            FrameHolder sp = null;
            var rect = new SDL_Rect();

            vp = VideoQueue.Last;
            if (vp.bmp != null)
            {
                if (SubtitleStream != null)
                {
                    if (SubtitleQueue.PendingCount > 0)
                    {
                        sp = SubtitleQueue.Current;
                        if (vp.Pts >= sp.Pts + ((float)sp.Subtitle.start_display_time / 1000))
                        {
                            if (!sp.IsUploaded)
                            {
                                byte** pixels = null;
                                int pitch = 0;

                                if (sp.PictureWidth == 0 || sp.PictureHeight == 0)
                                {
                                    sp.PictureWidth = vp.PictureWidth;
                                    sp.PictureHeight = vp.PictureHeight;
                                }

                                if (FFplay.realloc_texture(sub_texture, SDL_PIXELFORMAT_ARGB8888, sp.PictureWidth, sp.PictureHeight, SDL_BLENDMODE_BLEND, 1) < 0)
                                    return;

                                for (var i = 0; i < sp.Subtitle.num_rects; i++)
                                {
                                    AVSubtitleRect* sub_rect = sp.Subtitle.rects[i];
                                    sub_rect->x = ffmpeg.av_clip(sub_rect->x, 0, sp.PictureWidth);
                                    sub_rect->y = ffmpeg.av_clip(sub_rect->y, 0, sp.PictureHeight);
                                    sub_rect->w = ffmpeg.av_clip(sub_rect->w, 0, sp.PictureWidth - sub_rect->x);
                                    sub_rect->h = ffmpeg.av_clip(sub_rect->h, 0, sp.PictureHeight - sub_rect->y);

                                    SubtitleScaler = ffmpeg.sws_getCachedContext(SubtitleScaler,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_PAL8,
                                        sub_rect->w, sub_rect->h, AVPixelFormat.AV_PIX_FMT_BGRA,
                                        0, null, null, null);

                                    if (SubtitleScaler == null)
                                    {
                                        ffmpeg.av_log(null, ffmpeg.AV_LOG_FATAL, "Cannot initialize the conversion context\n");
                                        return;
                                    }

                                    if (SDL_LockTexture(sub_texture, sub_rect, pixels, &pitch) == 0)
                                    {
                                        var sourceData0 = sub_rect->data[0];
                                        var sourceStride = sub_rect->linesize[0];

                                        ffmpeg.sws_scale(SubtitleScaler, &sourceData0, &sourceStride,
                                              0, sub_rect->h, pixels, &pitch);

                                        SDL_UnlockTexture(sub_texture);
                                    }
                                }

                                sp.IsUploaded = true;
                            }
                        }
                        else
                        {
                            sp = null;
                        }
                    }
                }

                FFplay.calculate_display_rect(rect, xleft, ytop, PictureWidth, PictureHeight, vp.PictureWidth, vp.PictureHeight, vp.PictureAspectRatio);

                if (!vp.IsUploaded)
                {
                    fixed (SwsContext** ctx = &VideoScaler)
                    {
                        if (Player.upload_texture(vp.bmp, vp.DecodedFrame, ctx) < 0)
                            return;
                    }

                    vp.IsUploaded = true;
                }

                SDL_RenderCopy(Player.renderer, vp.bmp, null, rect);

                if (sp != null)
                {
                    SDL_RenderCopy(Player.renderer, sub_texture, null, rect);
                }
            }
        }

        public void StreamTogglePause()
        {
            if (IsPaused)
            {
                frame_timer += ffmpeg.av_gettime_relative() / 1000000.0 - VideoClock.LastUpdated;
                if (ReadPauseResult != ffmpeg.AVERROR_NOTSUPP)
                {
                    VideoClock.IsPaused = false;
                }

                VideoClock.SetPosition(VideoClock.Position, VideoClock.PacketSerial);
            }

            ExternalClock.SetPosition(ExternalClock.Position, ExternalClock.PacketSerial);
            IsPaused = AudioClock.IsPaused = VideoClock.IsPaused = ExternalClock.IsPaused = !IsPaused;
        }

        public void TogglePause()
        {
            StreamTogglePause();
            IsFrameStepping = false;
        }

        public void ToggleMute()
        {
            IsAudioMuted = !IsAudioMuted;
        }

        public void UpdateVolume(int sign, int step)
        {
            AudioVolume = ffmpeg.av_clip(AudioVolume + sign * step, 0, SDL_MIX_MAXVOLUME);
        }

        public void StepToNextFrame()
        {
            if (IsPaused)
                StreamTogglePause();
            IsFrameStepping = true;
        }

        public void UpdateVideoPts(double pts, long pos, int serial)
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
        public int SynchronizeAudio(int audioSampleCount)
        {
            var wantedAudioSampleCount = audioSampleCount;

            /* if not master, then */
            if (MasterSyncMode != SyncMode.Audio)
            {
                var audioSkew = AudioClock.Position - MasterClockPosition;

                if (!double.IsNaN(audioSkew) && Math.Abs(audioSkew) < Constants.AvNoSyncThreshold)
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

    }

}
