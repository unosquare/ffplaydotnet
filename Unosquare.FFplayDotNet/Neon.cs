using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Unosquare.FFplayDotNet.Core;
using Unosquare.FFplayDotNet.Primitives;
using Unosquare.Swan;

namespace Unosquare.FFplayDotNet
{
    public enum MediaType
    {
        Video = 0,
        Audio = 1,
        Subtitle = 3,
    }

    public abstract class NeonDecoderBase
    {
        #region Properties

        /// <summary>
        /// The is packet pending.
        /// Port of packet_pending.
        /// </summary>
        public bool IsPacketPending { get; private set; }

        /// <summary>
        /// Gets the packet serial.
        /// Port of pkt_serial
        /// </summary>
        public int PacketSerial { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the input decoding has been completed
        /// In other words, if there is no more packets in the queue to decode into frames.
        /// Port of finished
        /// </summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Gets the start PTS.
        /// Port of start_pts
        /// </summary>
        public long StartPts { get; internal set; }

        /// <summary>
        /// Gets the start PTS timebase.
        /// Port of start_pts_tb
        /// </summary>
        public AVRational StartPtsTimebase { get; internal set; }

        #endregion

    }

    public class NeonVideoDecoder
    {

        public NeonVideoDecoder(Neon parent)
        {

        }
    }

    public unsafe class Neon : IDisposable
    {
        #region Constants

        private const string ScanAllPMTsKey = "scan_all_pmts";
        private const string TitleKey = "title";

        #endregion

        #region Private Fields

        private bool IsDisposing = false;
        private readonly PlayerOptions Options = null;
        private readonly AVFormatContext* InputContext = null;
        private readonly Dictionary<MediaType, int> InputStreamIndexes;

        private bool SeekByBytes = false;
        private bool EnableInfiniteBuffer = false;
        private double MaxFrameDurationSeconds = 0d;
        private long MediaStartTimestamp = ffmpeg.AV_NOPTS_VALUE;

        #endregion

        #region Properties

        public string MediaUrl { get; private set; }

        public string InputFormatName { get; private set; }

        public bool InputAllowsDiscontinuities
        {
            get
            {
                if (InputContext == null) return false;
                return (InputContext->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0;
            }
        }

        public string MediaTitle
        {
            get
            {
                if (InputContext == null) return null;
                var optionEntry = FFDictionary.GetEntry(InputContext->metadata, TitleKey, false);
                return optionEntry?.Value;
            }
        }

        public bool IsMediaRealtime
        {
            get
            {
                if (InputContext == null)
                    return false;

                if (InputFormatName.Equals("rtp")
                   || InputFormatName.Equals("rtsp")
                   || InputFormatName.Equals("sdp")
                )
                    return true;

                if (InputContext->pb != null &&
                    (MediaUrl.StartsWith("rtp:") || MediaUrl.StartsWith("udp:")))
                    return true;

                return false;

            }
        }

        #endregion

        public Neon(string filename, string formatName = null)
        {

            // Argument Validation
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentNullException($"{nameof(filename)}");

            // Initialize the library (if not already done)
            Helper.RegisterFFmpeg();

            // Create the options object
            MediaUrl = filename;
            Options = new PlayerOptions();

            // Retrieve the input format (null = auto for default)
            AVInputFormat* inputFormat = null;
            if (string.IsNullOrWhiteSpace(formatName) == false)
            {
                inputFormat = ffmpeg.av_find_input_format(formatName);
                $"Format '{formatName}' not found. Will use automatic format detection.".Warn(typeof(Neon));
            }

            try
            {
                // Create the input format context, and open the input based on the provided format options.
                using (var formatOptions = new FFDictionary(Options.FormatOptions))
                {
                    if (formatOptions.HasKey(ScanAllPMTsKey) == false)
                        formatOptions.Set(ScanAllPMTsKey, "1", true);

                    // Allocate the input context and save it
                    var inputContext = ffmpeg.avformat_alloc_context();
                    InputContext = inputContext; // we save the InputContext as it will be used by other funtions (including dispose)

                    // Open the input file
                    var openResult = ffmpeg.avformat_open_input(&inputContext, MediaUrl, inputFormat, formatOptions.Reference);

                    // Validate the open operation
                    if (openResult < 0) throw new Exception($"Could not open '{MediaUrl}'. Error code: {openResult}");

                    // Set some general properties
                    InputContext = inputContext;
                    InputFormatName = Native.BytePtrToString(inputContext->iformat->name);

                    // If there are any optins left in the dictionary, it means they dod not get used (invalid options).
                    formatOptions.Remove(ScanAllPMTsKey);
                    if (formatOptions.First() != null)
                        $"Invalid format option: '{formatOptions.First()?.Key}'".Warn(typeof(Neon));
                }

                // Inject Codec Parameters
                if (Options.GeneratePts) InputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
                ffmpeg.av_format_inject_global_side_data(InputContext);

                // This is useful for file formats with no headers such as MPEG. This function also computes the real framerate in case of MPEG-2 repeat frame mode.
                if (ffmpeg.avformat_find_stream_info(InputContext, null) < 0)
                    $"{MediaUrl}: could read stream info.".Warn(typeof(Neon));

                // TODO: FIXME hack, ffplay maybe should not use avio_feof() to test for the end
                if (InputContext->pb != null) InputContext->pb->eof_reached = 0;

                // Setup initial state variables
                EnableInfiniteBuffer = IsMediaRealtime;
                SeekByBytes = InputAllowsDiscontinuities && (InputFormatName.Equals("ogg") == false);
                MaxFrameDurationSeconds = InputAllowsDiscontinuities ? 10d : 3600d;
                MediaStartTimestamp = InputContext->start_time;
                SeekToStartTimestamp();

                // Open the best suitable streams. Throw if no audio and/or video streams are found
                InputStreamIndexes = FindBestStreamIndexes();
                if (InputStreamIndexes.Count(s => s.Key == MediaType.Audio || s.Key == MediaType.Video) <= 0)
                    throw new Exception($"{MediaUrl}: No audio or video streams found to decode.");

                //foreach (var streamIndex in InputStreamIndexes)
                //    OpenStreamByIndex(streamIndex.Value);


            }
            catch (Exception ex)
            {
                $"Fatal error initializing {nameof(Neon)} instance.".Error(typeof(Neon), ex);
                Dispose(true);
                throw;
            }
        }

        //private void OpenStreamByIndex(int streamIndex)
        //{
        //    var input = InputContext;
        //    string forcedCodecName = null;
        //    FFDictionaryEntry kvp = null;

        //    var sampleRate = 0;
        //    var channelCount = 0;
        //    var channelLayout = 0L;
        //    var result = 0;

        //    int lowResIndex = Options.EnableLowRes ? 1 : 0;

        //    var codecContext = ffmpeg.avcodec_alloc_context3(null);

        //    result = ffmpeg.avcodec_parameters_to_context(codecContext, input->streams[streamIndex]->codecpar);
        //    if (result < 0)
        //    {
        //        ffmpeg.avcodec_free_context(&codecContext);
        //        throw new Exception
        //    }

        //    ffmpeg.av_codec_set_pkt_timebase(codecContext, input->streams[streamIndex]->time_base);
        //    var decoder = ffmpeg.avcodec_find_decoder(codecContext->codec_id);

        //    switch (codecContext->codec_type)
        //    {
        //        case AVMediaType.AVMEDIA_TYPE_AUDIO: LastAudioStreamIndex = streamIndex; forcedCodecName = Player.AudioCodecName; break;
        //        case AVMediaType.AVMEDIA_TYPE_SUBTITLE: LastSubtitleStreamIndex = streamIndex; forcedCodecName = Player.SubtitleCodecName; break;
        //        case AVMediaType.AVMEDIA_TYPE_VIDEO: LastVideoStreamIndex = streamIndex; forcedCodecName = Player.VideoCodecName; break;
        //    }

        //    if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
        //        decoder = ffmpeg.avcodec_find_decoder_by_name(forcedCodecName);

        //    if (decoder == null)
        //    {
        //        if (string.IsNullOrWhiteSpace(forcedCodecName) == false)
        //            ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with name '{forcedCodecName}'\n");
        //        else
        //            ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"No codec could be found with id {codecContext->codec_id}\n");

        //        result = ffmpeg.AVERROR_EINVAL;
        //        ffmpeg.avcodec_free_context(&codecContext);
        //        return result;
        //    }

        //    codecContext->codec_id = decoder->id;

        //    if (lowResIndex > ffmpeg.av_codec_get_max_lowres(decoder))
        //    {
        //        ffmpeg.av_log(codecContext, ffmpeg.AV_LOG_WARNING, $"The maximum value for lowres supported by the decoder is {ffmpeg.av_codec_get_max_lowres(decoder)}\n");
        //        lowResIndex = ffmpeg.av_codec_get_max_lowres(decoder);
        //    }

        //    if (Options.EnableLowRes)
        //    {
        //        ffmpeg.av_codec_set_lowres(codecContext, 1);
        //        codecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;
        //    }

        //    if (Options.EnableFastDecoding)
        //        codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        //    if ((decoder->capabilities & ffmpeg.AV_CODEC_CAP_DR1) != 0)
        //        codecContext->flags |= ffmpeg.CODEC_FLAG_EMU_EDGE;

        //    var codecOptions = Options.CodecOptions.FilterOptions(codecContext->codec_id, input, input->streams[streamIndex], decoder);

        //    if (codecOptions.HasKey("threads") == false)
        //        codecOptions["threads"] = "auto";

        //    if (Options.EnableLowRes)
        //        codecOptions["lowres"] = "1";

        //    if (codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
        //        codecOptions["refcounted_frames"] = "1";

        //    if ((result = ffmpeg.avcodec_open2(codecContext, decoder, codecOptions.Reference)) < 0)
        //    {
        //        ffmpeg.avcodec_free_context(&codecContext);
        //        return result;
        //    }

        //    // On return of avcodec_open2, codecOptions will be filled with options that were not found.
        //    if ((kvp = codecOptions.First()) != null)
        //    {
        //        ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, $"Option {kvp.Key} not found.\n");
        //        // Do not fail just because of an invalid option.
        //        //result = ffmpeg.AVERROR_OPTION_NOT_FOUND;
        //        //goto fail;
        //    }

        //    IsAtEndOfFile = false;
        //    input->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;

        //    switch (codecContext->codec_type)
        //    {
        //        case AVMediaType.AVMEDIA_TYPE_AUDIO:
        //            if ((result = Player.audio_open(channelLayout, channelCount, sampleRate, AudioOutputParams)) < 0)
        //            {
        //                ffmpeg.avcodec_free_context(&codecContext);
        //                return result;
        //            }

        //            AudioHardwareBufferSize = result;
        //            AudioOutputParams.CopyTo(AudioInputParams);
        //            RenderAudioBufferLength = 0;
        //            RenderAudioBufferIndex = 0;
        //            AudioSkewCoefficient = Math.Exp(Math.Log(0.01) / Constants.AudioSkewMinSamples);
        //            AudioSkewAvgCount = 0;
        //            AudioSkewThreshold = (double)(AudioHardwareBufferSize) / AudioOutputParams.BytesPerSecond;
        //            AudioStreamIndex = streamIndex;
        //            AudioStream = input->streams[streamIndex];

        //            AudioDecoder = new Decoder(this, codecContext, AudioPackets, IsFrameDecoded);

        //            if ((InputContext->iformat->flags & (ffmpeg.AVFMT_NOBINSEARCH | ffmpeg.AVFMT_NOGENSEARCH | ffmpeg.AVFMT_NO_BYTE_SEEK)) != 0 &&
        //                InputContext->iformat->read_seek.Pointer == IntPtr.Zero)
        //            {
        //                AudioDecoder.StartPts = AudioStream->start_time;
        //                AudioDecoder.StartPtsTimebase = AudioStream->time_base;
        //            }

        //            AudioDecoder.Start(FFplay.DecodeAudioQueueContinuously);
        //            SDL_PauseAudio(0);
        //            break;

        //        case AVMediaType.AVMEDIA_TYPE_VIDEO:
        //            VideoStreamIndex = streamIndex;
        //            VideoStream = input->streams[streamIndex];
        //            VideoDecoder = new Decoder(this, codecContext, VideoPackets, IsFrameDecoded);
        //            result = VideoDecoder.Start(FFplay.DecodeVideoQueueContinuously);
        //            EnqueuePacketAttachments = true;
        //            break;

        //        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
        //            SubtitleStreamIndex = streamIndex;
        //            SubtitleStream = input->streams[streamIndex];
        //            SubtitleDecoder = new Decoder(this, codecContext, SubtitlePackets, IsFrameDecoded);
        //            result = SubtitleDecoder.Start(FFplay.DecodeSubtitlesQueueContinuously);
        //            break;

        //        default:
        //            break;
        //    }

        //    return result;
        //}

        private Dictionary<MediaType, int> FindBestStreamIndexes()
        {
            var result = new Dictionary<MediaType, int>();
            var streamIndexes = new int[(int)AVMediaType.AVMEDIA_TYPE_NB];
            for (var i = 0; i < (int)AVMediaType.AVMEDIA_TYPE_NB; i++)
                streamIndexes[i] = -1;

            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
                ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], -1, null, 0);

            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] =
                ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO],
                                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO],
                                    null, 0);

            streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
                ffmpeg.av_find_best_stream(InputContext, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                    streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE],
                                    (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0 ?
                                     streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] :
                                     streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO]),
                                    null, 0);

            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
                result[MediaType.Video] = streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_VIDEO];

            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
                result[MediaType.Audio] = streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_AUDIO];

            if (streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
                result[MediaType.Subtitle] = streamIndexes[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE];

            return result;

        }

        private void SeekToStartTimestamp()
        {
            if (MediaStartTimestamp == ffmpeg.AV_NOPTS_VALUE)
                return;

            var seekToStartResult = ffmpeg.avformat_seek_file(InputContext, -1, long.MinValue, MediaStartTimestamp, long.MaxValue, 0);

            if (seekToStartResult < 0)
                $"File '{MediaUrl}'. Could not seek to position {MediaStartTimestamp / ffmpeg.AV_TIME_BASE}".Warn(typeof(Neon));

        }

        #region IDisposable Support


        protected virtual void Dispose(bool alsoManaged)
        {
            if (!IsDisposing)
            {
                if (alsoManaged)
                {
                    // TODO: dispose managed state (managed objects).
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                if (InputContext != null)
                    ffmpeg.avformat_free_context(InputContext);

                // TODO: set large fields to null.

                IsDisposing = true;
            }
        }

        ~Neon()
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
