namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using static Unosquare.FFplayDotNet.SDL;

    /// <summary>
    /// A class that is used to decode Audio, Video, or Subtitle packets
    /// into frames. Port of Decoder
    /// </summary>
    public unsafe partial class Decoder
    {

        #region Private Declarations

        /// <summary>
        /// Port of pkt_serial
        /// </summary>
        private int m_PacketSerial;

        /// <summary>
        /// The packet queue
        /// Port of *queue
        /// </summary>
        private readonly PacketQueue PacketQueue;

        /// <summary>
        /// The current packet
        /// Port of pkt
        /// </summary>
        private AVPacket CurrentPacket;

        private readonly LockCondition IsQueueEmpty;

        #endregion


        /// <summary>
        /// The codec context.
        /// Port of *avctx
        /// </summary>
        internal AVCodecContext* Codec;

        internal MediaState MediaState { get; private set; }

        #region Properties

        /// <summary>
        /// Gets the packet serial.
        /// Port of pkt_serial
        /// </summary>
        public int PacketSerial { get { return m_PacketSerial; } internal set { m_PacketSerial = value; } }

        /// <summary>
        /// Gets a value indicating whether the input decoding has been completed
        /// In other words, if there is no more packets to decode into frames.
        /// Port of finished
        /// </summary>
        public bool IsFinished { get; private set; }
        
        
        public long StartPts { get; internal set; }
        public AVRational StartPtsTimebase { get; internal set; }

        
        public SDL_Thread DecoderThread;


        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="Decoder"/> class.
        /// Port of decoder_init
        /// </summary>
        /// <param name="codecContext">The codec context.</param>
        /// <param name="queue">The queue.</param>
        /// <param name="isReadyForNextRead">The is ready for next read.</param>
        internal Decoder(MediaState mediaState, AVCodecContext* codecContext, PacketQueue queue, LockCondition isReadyForNextRead)
        {
            MediaState = mediaState;
            Codec = codecContext;
            PacketQueue = queue;
            IsQueueEmpty = isReadyForNextRead;
            StartPts = ffmpeg.AV_NOPTS_VALUE;
        }

        /// <summary>
        /// Decodes a video or audio frame
        /// Port of decoder_decode_frame
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <returns></returns>
        public int DecodeFrame(AVFrame* frame)
        {
            return DecodeFrame(frame, null);
        }

        /// <summary>
        /// Decodes a subtitle frame
        /// Port of decoder_decode_frame
        /// </summary>
        /// <param name="subtitle">The subtitle.</param>
        /// <returns></returns>
        public int DecodeFrame(AVSubtitle* subtitle)
        {
            return DecodeFrame(null, subtitle);
        }

        /// <summary>
        /// Decodes a video, audio or subtitle frame.
        /// Port of decoder_decode_frame
        /// </summary>
        /// <param name="outputFrame">The output frame.</param>
        /// <param name="subtitle">The subtitle.</param>
        /// <returns></returns>
        private int DecodeFrame(AVFrame* outputFrame, AVSubtitle* subtitle)
        {
            var gotFrame = default(int);
            var inputPacket = new AVPacket();
            var nextPtsTimebase = new AVRational();
            var nextPts = default(long);
            var isPacketPending = false;

            do
            {
                int result = -1;

                if (PacketQueue.IsAborted)
                    return -1;

                if (!isPacketPending || PacketQueue.Serial != PacketSerial)
                {
                    var queuePacket = new AVPacket();
                    do
                    {
                        if (PacketQueue.Count == 0)
                            IsQueueEmpty.Signal();

                        if (PacketQueue.Dequeue(&queuePacket, ref m_PacketSerial) < 0)
                            return -1;

                        if (queuePacket.data == PacketQueue.FlushPacket->data)
                        {
                            ffmpeg.avcodec_flush_buffers(Codec);
                            IsFinished = false;
                            nextPts = StartPts;
                            nextPtsTimebase = StartPtsTimebase;
                        }

                    } while (queuePacket.data == PacketQueue.FlushPacket->data || PacketQueue.Serial != PacketSerial);

                    fixed (AVPacket* currentPacketPtr = &CurrentPacket)
                        ffmpeg.av_packet_unref(currentPacketPtr);

                    CurrentPacket = queuePacket;
                    inputPacket = CurrentPacket;

                    isPacketPending = true;
                }

                switch (Codec->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
#pragma warning disable CS0618 // Type or member is obsolete
                        result = ffmpeg.avcodec_decode_video2(Codec, outputFrame, &gotFrame, &inputPacket);
#pragma warning restore CS0618 // Type or member is obsolete

                        if (gotFrame != 0)
                        {
                            if (MediaState.IsPtsReorderingEnabled.HasValue == false)
                            {
                                outputFrame->pts = ffmpeg.av_frame_get_best_effort_timestamp(outputFrame);
                            }
                            else if (MediaState.IsPtsReorderingEnabled.HasValue && MediaState.IsPtsReorderingEnabled.Value == false)
                            {
                                outputFrame->pts = outputFrame->pkt_dts;
                            }
                        }

                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
#pragma warning disable CS0618 // Type or member is obsolete
                        result = ffmpeg.avcodec_decode_audio4(Codec, outputFrame, &gotFrame, &inputPacket);
#pragma warning restore CS0618 // Type or member is obsolete

                        if (gotFrame != 0)
                        {
                            var audioTimebase = new AVRational { num = 1, den = outputFrame->sample_rate };
                            if (outputFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                                outputFrame->pts = ffmpeg.av_rescale_q(outputFrame->pts, ffmpeg.av_codec_get_pkt_timebase(Codec), audioTimebase);
                            else if (nextPts != ffmpeg.AV_NOPTS_VALUE)
                                outputFrame->pts = ffmpeg.av_rescale_q(nextPts, nextPtsTimebase, audioTimebase);
                            if (outputFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                            {
                                nextPts = outputFrame->pts + outputFrame->nb_samples;
                                nextPtsTimebase = audioTimebase;
                            }
                        }

                        break;

                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        result = ffmpeg.avcodec_decode_subtitle2(Codec, subtitle, &gotFrame, &inputPacket);
                        break;
                }

                if (result < 0)
                {
                    isPacketPending = false;
                }
                else
                {
                    inputPacket.dts =
                    inputPacket.pts = ffmpeg.AV_NOPTS_VALUE;

                    if (inputPacket.data != null)
                    {
                        if (Codec->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                            result = inputPacket.size;

                        inputPacket.data += result;
                        inputPacket.size -= result;
                        if (inputPacket.size <= 0)
                            isPacketPending = false;
                    }
                    else
                    {
                        if (gotFrame == 0)
                        {
                            isPacketPending = false;
                            IsFinished = Convert.ToBoolean(PacketSerial);
                        }
                    }
                }

            } while (!Convert.ToBoolean(gotFrame) && !IsFinished);

            return gotFrame;
        }

        public void DecoderDestroy()
        {
            fixed (AVPacket* packetPtr = &CurrentPacket)
                ffmpeg.av_packet_unref(packetPtr);

            fixed (AVCodecContext** codecPtr = &Codec)
                ffmpeg.avcodec_free_context(codecPtr);
        }

        public void DecoderAbort(FrameQueue fq)
        {
            PacketQueue.Abort();
            fq.SignalDoneWriting(null);
            SDL_WaitThread(DecoderThread, null);
            DecoderThread = null;
            PacketQueue.Clear();
        }

    }

}
