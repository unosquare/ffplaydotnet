namespace Unosquare.FFplayDotNet.Sample
{
    using System.Runtime.InteropServices;

    static public class Native
    {
        [DllImport("kernel32")]
        public static extern void AllocConsole();

        [DllImport("kernel32")]
        public static extern void FreeConsole();
    }
}
