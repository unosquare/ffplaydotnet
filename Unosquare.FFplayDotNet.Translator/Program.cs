using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Unosquare.Swan;

namespace Unosquare.FFplayDotNet.Translator
{
    public class Program
    {




        static void Main(string[] args)
        {
            var converter = new CodeConverter();
            converter.Run();
        }
    }


    public class CodeConverter
    {
        public const string SourceCodeDownloadUrl = "https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/3.2/ffplay.c";

        public static string SourceFilePath { get; } = Runtime.GetDesktopFilePath("ffplay.c");

        public CodeConverter()
        {

        }

        public void Run()
        {
            var output = new List<string>();
            output.Add("namespace Unosquare.FFplayDotNet");
            output.Add("{");

            output.Add("    using FFmpeg.AutoGen;");
            output.Add("    using System;");
            output.Add("    using System.Runtime.InteropServices;");
            output.Add("    using System.Threading;");
            output.Add("    using System.Windows;");
            output.Add("    using System.Windows.Media.Imaging;");
            output.Add("");

            output.Add("    internal unsafe partial class FFplay");
            output.Add("    {");

            var lines = LoadSourceFile(SourceCodeDownloadUrl, SourceFilePath);


            lines = ProcessCleanup(lines);

            {
                var stepOutput = new List<string>();
                lines = ProcessExtractDefines(lines, stepOutput);
                output.AddRange(stepOutput);
            }


            {
                var stepOutput = new List<string>();
                lines = ProcessExtractEnums(lines, stepOutput);
                output.AddRange(stepOutput);
            }

            {
                var stepOutput = new List<string>();
                lines = ProcessExtractTypedfs(lines, stepOutput);
                output.AddRange(stepOutput);
            }

            {
                var stepOutput = new List<string>();
                lines = ProcessExtractProperties(lines, stepOutput);
                output.AddRange(stepOutput);
            }


            output.Add("    }");
            output.Add("}");

            var result = PerfromFinalReplacements(output);
            //var result = CodeReference.Parse(0, lines);

        }

        private static string[] ProcessCleanup(string[] lines)
        {
            var resultingLines = new List<string>();
            var codeStack = new CodeStack();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var lineParts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                var linePartsCount = lineParts.Length;

                if (line.Equals("static void free_picture(Frame *vp);")) continue;
                if (line.StartsWith("#include ")) continue;

                { // If and end if
                    if (line.Contains("/*") && line.EndsWith("*/"))
                        continue;

                    if (line.StartsWith("/*"))
                    {
                        codeStack.Push("/*", "");
                        continue;
                    }

                    if (line.Contains("*/") && codeStack.Count > 0 && codeStack.Peek().Key.Equals("/*"))
                    {
                        codeStack.Pop();
                        continue;
                    }

                    if (codeStack.Contains("/*", ""))
                        continue;
                }

                { // If and end if
                    if (line.StartsWith("#if "))
                    {
                        codeStack.Push(lineParts[0], lineParts[1]);
                        continue;
                    }

                    if (line.StartsWith("#endif") && codeStack.Count > 0 && codeStack.Peek().Key.Equals("#if"))
                    {
                        codeStack.Pop();
                        continue;
                    }

                    if (codeStack.Contains("#if", "CONFIG_AVFILTER"))
                        continue;
                }

                var lineToAdd = lines[i];
                lineToAdd = lineToAdd.Replace("unsigned int", "uint");
                lineToAdd = lineToAdd.Replace("unsigned", "uint");

                resultingLines.Add(lineToAdd);
            }

            return resultingLines.ToArray();
        }

        private static string[] ProcessExtractDefines(string[] lines, List<string> output)
        {
            var resultingLines = new List<string>();
            var codeStack = new CodeStack();

            output.Add("");
            output.Add($"        #region Constant Definitions");

            output.Add("        internal const int SDL_MIX_MAXVOLUME = 128; // http://www.libsdl.org/tmp/SDL/include/SDL_audio.h");
            output.Add("        internal const int SDL_USEREVENT = 0x8000; // https://www.libsdl.org/tmp/SDL/include/SDL_events.h");

            output.Add("");

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var lineParts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                var linePartsCount = lineParts.Length;

                { // defines
                    if (line.StartsWith("#define ") && linePartsCount >= 3)
                    {
                        lineParts = line.Split(new char[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                        output.Add($"        internal const {(lineParts[2].Contains(".") ? "double" : "int")} {lineParts[1]} = {lineParts[2]};");
                        continue;
                    }
                }

                resultingLines.Add(lines[i]);
            }

            output.Add($"        #endregion");

            return resultingLines.ToArray();
        }

        private static string[] ProcessExtractProperties(string[] lines, List<string> output)
        {
            var resultingLines = new List<string>();
            var codeStack = new CodeStack();

            output.Add("");
            output.Add($"        #region Properties");

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var lineParts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                var linePartsCount = lineParts.Length;

                { // defines
                    if (lines[i].StartsWith("static ") && line.EndsWith(";"))
                    {
                        line = line.Replace("const char *", "string ");
                        line = line.Replace("const char* ", "string ");
                        line = line.Replace("const int ", "int[] ");
                        line = line.Replace("static ", "");
                        line = line.TrimEnd(';');

                        lineParts = line.Split(new char[] { ' ', '=' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                        // type = 0 name = 1 value = 2

                        output.Add($"        internal {lineParts[0]} {lineParts[1]} {{ get; set; }}{(lineParts.Length >= 3 ? " = " + lineParts[2] + ";" : "")}");
                        continue;
                    }
                }

                resultingLines.Add(lines[i]);
            }

            output.Add($"        #endregion");

            return resultingLines.ToArray();
        }

        private static string[] ProcessExtractEnums(string[] lines, List<string> output)
        {
            var resultingLines = new List<string>();
            var codeStack = new CodeStack();

            output.Add("");
            output.Add("        #region Enumerations");

            output.Add("        internal enum SyncMode");
            output.Add("        {");
            output.Add("            AV_SYNC_AUDIO_MASTER,");
            output.Add("            AV_SYNC_VIDEO_MASTER,");
            output.Add("            AV_SYNC_EXTERNAL_CLOCK,");
            output.Add("        }");
            output.Add("");

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var lineParts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                var linePartsCount = lineParts.Length;

                { // enums
                    if (line.StartsWith("enum ") && line.EndsWith("{") && lineParts.Length >= 3)
                    {
                        resultingLines.Add(lines[i].TrimEnd('{'));
                        output.Add($"        internal enum {lineParts[1]}");
                        output.Add($"        {{");
                        codeStack.Push("enum", lineParts[1]);
                        continue;
                    }

                    if (codeStack.Count >= 1 && codeStack.Peek().Key.Equals("enum"))
                    {
                        if (line.StartsWith("}") && line.EndsWith(";"))
                        {
                            resultingLines[resultingLines.Count - 1] = "    " + codeStack.Peek().Value + " " + lineParts[1];

                            output.Add($"        }}");
                            output.Add("");
                            codeStack.Pop();

                            continue;
                        }
                        else
                        {
                            output.Add($"        {lines[i]}");
                            continue;
                        }
                    }
                }

                resultingLines.Add(lines[i]);
            }

            output.Add("        #endregion");

            return resultingLines.ToArray();
        }

        private static string[] ProcessExtractTypedfs(string[] lines, List<string> output)
        {
            var resultingLines = new List<string>();
            var codeStack = new CodeStack();

            output.Add("");
            output.Add($"        #region Supporting Classes");

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var lineParts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                var linePartsCount = lineParts.Length;

                { // typedef struct

                    if (line.StartsWith("typedef struct "))
                    {
                        output.Add($"        internal class {lineParts[2]}");
                        output.Add($"        {{");
                        codeStack.Push("typedef struct", "lineParts[2]");
                        continue;
                    }

                    if (codeStack.Count >= 1 && codeStack.Peek().Key.Equals("typedef struct"))
                    {
                        if (line.StartsWith("}") && line.EndsWith(";"))
                        {
                            output.Add($"        }}");
                            output.Add("");
                            codeStack.Pop();
                            continue;
                        }
                        else
                        {
                            output.Add($"            public {lines[i].TrimStart()}");
                            continue;
                        }
                    }
                }

                resultingLines.Add(lines[i]);
            }

            output.Add("        #endregion");

            return resultingLines.ToArray();
        }

        private static string PerfromFinalReplacements(List<string> output)
        {
            var allText = string.Join("\r\n", output);

            allText = allText.Replace("FFMAX(", "Math.Max(");
            allText = allText.Replace("int64_t", "long");
            allText = allText.Replace("const char *", "string ");
            allText = allText.Replace("internal const int FRAME_QUEUE_SIZE", "internal static readonly int FRAME_QUEUE_SIZE");
            allText = allText.Replace("public struct MyAVPacketList *next;", "public MyAVPacketList next;");
            allText = allText.Replace("public MyAVPacketList *first_pkt, *last_pkt;", "public MyAVPacketList first_pkt;\r\n            public MyAVPacketList last_pkt;");
            allText = allText.Replace("public enum AVSampleFormat fmt;", "public AVSampleFormat fmt;");

            allText = allText.Replace("public Frame queue[FRAME_QUEUE_SIZE];", "public Frame[] queue = new Frame[FRAME_QUEUE_SIZE];");
            allText = allText.Replace("public PacketQueue* pktq;", "public PacketQueue pktq;");

            allText = allText.Replace("public uint8_t *audio_buf;", "public IntPtr audio_buf;");
            allText = allText.Replace("public uint8_t *audio_buf1", "public IntPtr audio_buf1;");

            allText = allText.Replace("public struct AudioParams ", "public AudioParams ");
            allText = allText.Replace("public struct SwsContext *", "public SwsContext *");
            allText = allText.Replace("int16_t", "short");
            allText = allText.Replace("public char *filename;", "public string filename;");

            allText = allText.Replace("internal uint sws_flags { get; set; } = SWS_BICUBIC;", "internal uint sws_flags { get; set; } = (uint)ffmpeg.SWS_BICUBIC;");
            allText = allText.Replace("internal string wanted_stream_spec[AVMEDIA_TYPE_NB] { get; set; } = {0};", "internal string[] wanted_stream_spec = new string[(int)AVMediaType.AVMEDIA_TYPE_NB];");
            allText = allText.Replace("internal enum ShowMode { get; set; } = show_mode;", "internal ShowMode show_mode { get; set; } = ShowMode.SHOW_MODE_VIDEO;");

            allText = allText.Replace("public PacketQueue *", "public PacketQueue ");
            allText = allText.Replace("public short sample_array[SAMPLE_ARRAY_SIZE];", "public short[] sample_array = new short[SAMPLE_ARRAY_SIZE];");

            allText = allText.Replace("AV_NOPTS_VALUE", "ffmpeg.AV_NOPTS_VALUE");
            allText = allText.Replace("AV_SYNC_", "SyncMode.AV_SYNC_");

            return allText;
        }

        private static string[] LoadSourceFile(string sourceCodeDownloadUrl, string sourceFilePath)
        {
            if (File.Exists(sourceFilePath) == false)
            {
                $"File '{sourceFilePath}' does not exist.".Info();
                $"Downloading from '{sourceCodeDownloadUrl}' does not exist.".Info();
                var request = WebRequest.Create(sourceCodeDownloadUrl) as HttpWebRequest;
                request.Method = "GET";
                using (var response = request.GetResponse())
                {
                    using (var readStream = response.GetResponseStream())
                    {
                        using (var writeStream = File.OpenWrite(sourceFilePath))
                        {
                            readStream.CopyTo(writeStream);
                        }
                    }
                }

                $"File '{sourceFilePath}' is now copied.".Info();
            }

            return File.ReadAllLines(sourceFilePath);
        }
    }

    public class CodeStack : Stack<KeyValuePair<string, string>>
    {
        public void Push(string construct, string text)
        {
            Push(new KeyValuePair<string, string>(construct, text));
        }

        public bool Contains(string construct, string text)
        {
            return this.Any(item => item.Key.Equals(construct) && item.Value.Equals(text));
        }
    }

}
