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
        public AVPacket CurrentPacket;
        public AVPacket pkt_temp;
        internal PacketQueue PacketQueue;
        public AVCodecContext* Codec;
        public int PacketSerial;
        public bool IsFinished;
        public bool IsPacketPending;
        public SDL_cond empty_queue_cond;
        public long StartPts;
        public AVRational StartPtsTimebase;
        public long NextPts;
        public AVRational NextPtsTimebase;
        public SDL_Thread DecoderThread;
        public bool? IsPtsReorderingEnabled { get; private set; } = null;


        internal Decoder(AVCodecContext* avctx, PacketQueue queue, SDL_cond empty_queue_cond3)
        {
            Codec = avctx;
            PacketQueue = queue;
            empty_queue_cond = empty_queue_cond3;
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

        private int DecodeFrame(AVFrame* frame, AVSubtitle* subtitle)
        {
            int got_frame = 0;
            do
            {
                int ret = -1;
                if (PacketQueue.IsAborted)
                    return -1;

                if (!IsPacketPending || PacketQueue.Serial != PacketSerial)
                {
                    var pkt = new AVPacket();
                    do
                    {
                        if (PacketQueue.Length == 0)
                            SDL_CondSignal(empty_queue_cond);
                        if (PacketQueue.Dequeue(&pkt, ref PacketSerial) < 0)
                            return -1;
                        if (pkt.data == PacketQueue.FlushPacket->data)
                        {
                            ffmpeg.avcodec_flush_buffers(Codec);
                            IsFinished = false;
                            NextPts = StartPts;
                            NextPtsTimebase = StartPtsTimebase;
                        }
                    } while (pkt.data == PacketQueue.FlushPacket->data || PacketQueue.Serial != PacketSerial);
                    fixed (AVPacket* refPacket = &CurrentPacket)
                    {
                        ffmpeg.av_packet_unref(refPacket);
                    }

                    pkt_temp = CurrentPacket = pkt;
                    IsPacketPending = true;
                }
                switch (Codec->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        fixed (AVPacket* pktTemp = &pkt_temp)
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            ret = ffmpeg.avcodec_decode_video2(Codec, frame, &got_frame, pktTemp);
#pragma warning restore CS0618 // Type or member is obsolete
                        }
                        if (got_frame != 0)
                        {
                            if (IsPtsReorderingEnabled.HasValue == false)
                            {
                                frame->pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
                            }
                            else if (IsPtsReorderingEnabled.HasValue && IsPtsReorderingEnabled.Value == false)
                            {
                                frame->pts = frame->pkt_dts;
                            }
                        }
                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        fixed (AVPacket* pktTemp = &pkt_temp)
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            ret = ffmpeg.avcodec_decode_audio4(Codec, frame, &got_frame, pktTemp);
#pragma warning restore CS0618 // Type or member is obsolete
                        }
                        if (got_frame != 0)
                        {
                            var tb = new AVRational { num = 1, den = frame->sample_rate };
                            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(frame->pts, ffmpeg.av_codec_get_pkt_timebase(Codec), tb);
                            else if (NextPts != ffmpeg.AV_NOPTS_VALUE)
                                frame->pts = ffmpeg.av_rescale_q(NextPts, NextPtsTimebase, tb);
                            if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
                            {
                                NextPts = frame->pts + frame->nb_samples;
                                NextPtsTimebase = tb;
                            }
                        }
                        break;
                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        fixed (AVPacket* pktTemp = &pkt_temp)
                        {
                            ret = ffmpeg.avcodec_decode_subtitle2(Codec, subtitle, &got_frame, pktTemp);
                        }
                        break;
                }
                if (ret < 0)
                {
                    IsPacketPending = false;
                }
                else
                {
                    pkt_temp.dts =
                    pkt_temp.pts = ffmpeg.AV_NOPTS_VALUE;
                    if (pkt_temp.data != null)
                    {
                        if (Codec->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                            ret = pkt_temp.size;

                        pkt_temp.data += ret;
                        pkt_temp.size -= ret;
                        if (pkt_temp.size <= 0)
                            IsPacketPending = false;
                    }
                    else
                    {
                        if (got_frame == 0)
                        {
                            IsPacketPending = false;
                            IsFinished = Convert.ToBoolean(PacketSerial);
                        }
                    }
                }
            } while (!Convert.ToBoolean(got_frame) && !IsFinished);

            return got_frame;
        }

        public void DecoderDestroy()
        {
            fixed (AVPacket* packetRef = &CurrentPacket)
            {
                ffmpeg.av_packet_unref(packetRef);
            }

            fixed (AVCodecContext** refContext = &Codec)
            {
                ffmpeg.avcodec_free_context(refContext);
            }
        }

        public void DecoderAbort(FrameQueue fq)
        {
            PacketQueue.Abort();
            fq.frame_queue_signal();
            SDL_WaitThread(DecoderThread, null);
            DecoderThread = null;
            PacketQueue.Clear();
        }

    }

}
