using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Unosquare.FFplayDotNet.SDL;

namespace Unosquare.FFplayDotNet
{

    public unsafe class Decoder
    {
        public int m_PacketSerial;

        public int PacketSerial { get { return m_PacketSerial; } internal set { m_PacketSerial = value; } }

        public AVPacket CurrentPacket;
        internal PacketQueue PacketQueue;
        public AVCodecContext* Codec;

        public bool IsFinished;
        public bool IsPacketPending;
        
        public long StartPts;
        public AVRational StartPtsTimebase;

        private LockCondition IsQueueEmpty;
        public SDL_Thread DecoderThread;
        public bool? IsPtsReorderingEnabled { get; private set; } = null;


        internal Decoder(AVCodecContext* codecContext, PacketQueue queue, LockCondition isReadyForNextRead)
        {
            Codec = codecContext;
            PacketQueue = queue;
            IsQueueEmpty = isReadyForNextRead;
            StartPts = ffmpeg.AV_NOPTS_VALUE;
        }

        public int DecodeFrame(AVFrame* frame)
        {
            return DecodeFrame(frame, null);
        }

        public int DecodeFrame(AVSubtitle* subtitle)
        {
            return DecodeFrame(null, subtitle);
        }

        private int DecodeFrame(AVFrame* outputFrame, AVSubtitle* subtitle)
        {
            var gotFrame = default(int);
            var inputPacket = new AVPacket();
            var nextPtsTimebase = new AVRational();
            var nextPts = default(long);

            do
            {
                int result = -1;

                if (PacketQueue.IsAborted)
                    return -1;

                if (!IsPacketPending || PacketQueue.Serial != PacketSerial)
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

                    inputPacket = CurrentPacket = queuePacket;
                    IsPacketPending = true;
                }

                switch (Codec->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
#pragma warning disable CS0618 // Type or member is obsolete
                        result = ffmpeg.avcodec_decode_video2(Codec, outputFrame, &gotFrame, &inputPacket);
#pragma warning restore CS0618 // Type or member is obsolete

                        if (gotFrame != 0)
                        {
                            if (IsPtsReorderingEnabled.HasValue == false)
                            {
                                outputFrame->pts = ffmpeg.av_frame_get_best_effort_timestamp(outputFrame);
                            }
                            else if (IsPtsReorderingEnabled.HasValue && IsPtsReorderingEnabled.Value == false)
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
                            var tb = new AVRational { num = 1, den = outputFrame->sample_rate };
                            if (outputFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                                outputFrame->pts = ffmpeg.av_rescale_q(outputFrame->pts, ffmpeg.av_codec_get_pkt_timebase(Codec), tb);
                            else if (nextPts != ffmpeg.AV_NOPTS_VALUE)
                                outputFrame->pts = ffmpeg.av_rescale_q(nextPts, nextPtsTimebase, tb);
                            if (outputFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                            {
                                nextPts = outputFrame->pts + outputFrame->nb_samples;
                                nextPtsTimebase = tb;
                            }
                        }

                        break;

                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        result = ffmpeg.avcodec_decode_subtitle2(Codec, subtitle, &gotFrame, &inputPacket);
                        break;
                }

                if (result < 0)
                {
                    IsPacketPending = false;
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
                            IsPacketPending = false;
                    }
                    else
                    {
                        if (gotFrame == 0)
                        {
                            IsPacketPending = false;
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
