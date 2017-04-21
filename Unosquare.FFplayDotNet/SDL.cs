namespace Unosquare.FFplayDotNet
{
    using FFmpeg.AutoGen;
    using System;
    using System.Threading;

    static public unsafe class SDL
    {

        public delegate void SDL_AudioCallback(MediaState vs, byte* stream, int len);

        #region Dummy Constants

        internal const int SDL_MIX_MAXVOLUME = 128; // http://www.libsdl.org/tmp/SDL/include/SDL_audio.h
        internal const int SDL_USEREVENT = 0x8000; // https://www.libsdl.org/tmp/SDL/include/SDL_events.h
        internal const uint SDL_PIXELFORMAT_ARGB8888 = 0; // TODO: https://github.com/jaz303/mark-sweep/blob/master/binding-generator/headers/SDL_pixels.h
        internal const uint SDL_PIXELFORMAT_YV12 = 1; // TODO: pixel format is fdummy
        internal const uint SDL_BLENDMODE_NONE = 0; // http://www-personal.umich.edu/~bazald/l/api/_s_d_l__blendmode_8h.html
        internal const uint SDL_BLENDMODE_BLEND = 1; // http://www-personal.umich.edu/~bazald/l/api/_s_d_l__blendmode_8h.html

        internal const int SDL_WINDOW_SHOWN = 1; // TODO: Verify
        internal const int SDL_WINDOW_RESIZABLE = 2; // TODO: Verify
        internal const int SDL_WINDOWPOS_UNDEFINED = 2; // TODO: Verify
        internal const int SDL_WINDOW_FULLSCREEN_DESKTOP = 4; // TODO: verify
        internal const int SDL_RENDERER_ACCELERATED = 4; // TODO: verify
        internal const int SDL_RENDERER_PRESENTVSYNC = 4; // TODO: verify
        internal const int SDL_HINT_RENDER_SCALE_QUALITY = 100; // TODO: Verify
        internal const int SDL_GETEVENT = 1;
        internal const int AUDIO_S16SYS = 16;

        #endregion

        #region SDL Placeholders



        public class SDL_Thread { }
        public class SDL_Texture { }

        public class SDL_Window { }
        public class SDL_Renderer { }
        public class SDL_Rect { public int x; public int y; public int w; public int h; }
        public class SDL_RendererInfo { }
        public class SDL_Event { public int type; public object user_data1; }
        public class SDL_AudioSpec { public int channels; public int freq; public int format; public int silence; public int samples; public MediaState userdata; public SDL_AudioCallback callback; public int size; }


        public static void SDL_WaitThread(SDL_Thread thread, object args) { }
        public static void SDL_RenderFillRect(object renderer, SDL_Rect rect) { }
        public static int SDL_UpdateYUVTexture(SDL_Texture texture, object other, byte* a, int a1, byte* b, int b1, byte* c, int c1) { return 0; }
        public static int SDL_UpdateTexture(SDL_Texture tex, object other, byte* a, int a1) { return 0; }
        public static void SDL_UnlockTexture(SDL_Texture tex) { }
        public static int SDL_LockTexture(SDL_Texture tex, AVSubtitleRect* other, byte** pixels, int* pitch) { return 0; }
        public static void SDL_SetRenderDrawColor(SDL_Renderer renderer, byte a, byte r, byte g, byte b) { }
        public static void SDL_RenderCopy(SDL_Renderer renderer, SDL_Texture texture, AVSubtitleRect* a, SDL_Rect b) { }
        public static void SDL_CloseAudio() { }
        public static void SDL_DestroyTexture(SDL_Texture texture) { }
        public static void SDL_DestroyRenderer(SDL_Renderer renderer) { }
        public static void SDL_DestroyWindow(SDL_Window window) { }
        public static void SDL_Quit() { }
        public static void SDL_RenderClear(SDL_Renderer renderer) { }
        public static void SDL_RenderPresent(SDL_Renderer renderer) { }
        public static SDL_Window SDL_CreateWindow(string window_title, int flags1, int flags2, int w, int h, int flags3) { return new SDL_Window(); }
        public static void SDL_SetHint(int key, string value) { }
        public static SDL_Renderer SDL_CreateRenderer(SDL_Window window, int z, int flags) { return new SDL_Renderer(); }
        public static int SDL_GetRendererInfo(SDL_Renderer renderer, SDL_RendererInfo info) { return 0; }
        public static int SDL_SetWindowSize(SDL_Window win, int w, int h) { return 0; }
        public static void SDL_PushEvent(SDL_Event ev) { }
        public static int SDL_PeepEvents(SDL_Event ev, int a, int op, int user1, int user2) { return 0; }
        public static int SDL_MixAudio(byte* stream, byte* buffer, int len, int volume) { return 0; }
        public static string SDL_getenv(string name) { return "2"; }
        public static int SDL_OpenAudio(SDL_AudioSpec wanted, SDL_AudioSpec output) { return 0; }
        public static string SDL_GetError() { return string.Empty; }
        public static void SDL_PauseAudio(int delay) { }
        public static SDL_Thread SDL_CreateThread(Func<MediaState, int> fn, MediaState vst) { return new SDL_Thread(); }

        #endregion

    }
}
