using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

/// <summary>
/// 捕获指定进程主窗口的位图内容。
/// WinUI 3 基于 DirectComposition，普通 BitBlt 会得到空白，
/// 因此这里使用 PrintWindow + PW_RENDERFULLCONTENT，窗口被遮挡也能取到画面
/// （已验证可正确取得 WebGL 背景图层）。
///
/// 用法：WinCapture.exe &lt;进程名&gt; &lt;输出png路径&gt;
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：WinCapture.exe <进程名> <输出png路径>");
            return 2;
        }

        string processName = args[0];
        string outPath = args[1];

        Process? proc = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

        if (proc is null)
        {
            Console.WriteLine($"NOT_FOUND: 未找到带主窗口的进程 {processName}");
            return 1;
        }

        IntPtr hwnd = proc.MainWindowHandle;
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            Console.WriteLine("GET_RECT_FAILED");
            return 1;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"BAD_RECT: {width}x{height}");
            return 1;
        }

        using var bmp = new Bitmap(width, height);
        using (Graphics gfx = Graphics.FromImage(bmp))
        {
            IntPtr hdc = gfx.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            gfx.ReleaseHdc(hdc);
            Console.WriteLine($"PrintWindow={ok}");
        }

        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"size={width}x{height} -> {outPath}");
        proc.Dispose();
        return 0;
    }
}
