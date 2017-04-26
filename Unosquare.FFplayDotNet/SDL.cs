namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using Unosquare.FFplayDotNet.Primitives;

    static public unsafe class SDL
    {

        public delegate void SDL_AudioCallback(MediaState vs, byte* stream, int len);

        #region Dummy Constants

        internal const int SDL_MIX_MAXVOLUME = 128; // http://www.libsdl.org/tmp/SDL/include/SDL_audio.h
        internal const int AUDIO_S16SYS = 16;

        #endregion

        #region SDL Placeholders

        public class SDL_AudioSpec { public int channels; public int freq; public int format; public int silence; public int samples; public MediaState userdata; public SDL_AudioCallback callback; public int size; }

        public static void SDL_UnlockTexture(BitmapBuffer tex) { }
        public static int SDL_LockTexture(BitmapBuffer tex, AVSubtitleRect* other, byte** pixels, int* pitch) { return 0; }

        public static void SDL_CloseAudio() { }
        public static void SDL_Quit() { }


        public static int SDL_MixAudio(byte* stream, byte* buffer, int len, int volume) { return 0; }
        public static string SDL_getenv(string name) { return "2"; }
        public static int SDL_OpenAudio(SDL_AudioSpec wanted, SDL_AudioSpec output) { return 0; }
        public static string SDL_GetError() { return string.Empty; }
        public static void SDL_PauseAudio(int delay) { }

        #endregion

    }
}
