using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

/// <summary>
/// 验证导出页的输出操作在「恶意文件名」下安全可用。
///
/// 测试文件由本程序自行创建（避免路径经 shell 传递时被转义破坏），
/// 文件名含空格、中文、双引号与 &amp; 等 shell 元字符；随后把该路径填入
/// 界面的输出框，点击「打开输出目录」与「播放成片」，检查是否发生命令注入。
///
/// 用法：OutputActionTest.exe &lt;进程名&gt; [--keep]
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static int Main(string[] args)
    {
        string processName = args.Length > 0 ? args[0] : "AmllMvStudio";
        bool keep = args.Contains("--keep");

        // 1) 自行创建含 shell 元字符的测试文件
        //    注入一旦生效，文件名里的 & calc & 会让 shell 执行 calc。
        //    注意：Windows 文件名不允许出现双引号（会抛“语法不正确”），
        //    所以这里只用「合法但危险」的字符：& ^ ( ) ! 空格 与中文。
        string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "out", "安全测试 & 注入");
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        string hostileName = "成片 & calc & 结束 ^ (v1).mp4";
        string target = Path.Combine(dir, hostileName);
        File.WriteAllText(target, "占位内容，仅用于验证输出操作不会执行文件");

        Console.WriteLine("测试文件：");
        Console.WriteLine("  " + target);
        Console.WriteLine("  存在 = " + File.Exists(target));
        Console.WriteLine();

        Process? proc = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc is null)
        {
            Console.WriteLine("NOT_FOUND");
            return 1;
        }

        IntPtr hwnd = proc.MainWindowHandle;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        for (int i = 0; i < 20 && GetForegroundWindow() != hwnd; i++)
        {
            Thread.Sleep(200);
            SetForegroundWindow(hwnd);
        }

        AutomationElement? window = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id));
        if (window is null)
        {
            Console.WriteLine("找不到主窗口");
            return 1;
        }

        // 2) 切到导出页
        AutomationElement? nav = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "导出成片"));
        if (nav is null)
        {
            Console.WriteLine("找不到导出成片导航项");
            return 1;
        }

        for (int i = 0; i < 15; i++)
        {
            ClickByMouse(nav);
            Thread.Sleep(700);
            AutomationElement? boxProbe = FindByAutomationId(window, "OutputPathBox");
            if (boxProbe is not null)
            {
                System.Windows.Rect r = boxProbe.Current.BoundingRectangle;
                if (r.Width > 0 && r.Height > 0) break;
            }
        }

        // 3) 把恶意路径填入输出框
        AutomationElement? box = FindByAutomationId(window, "OutputPathBox");
        if (box is null)
        {
            Console.WriteLine("未找到输出框");
            return 1;
        }

        bool filled = false;
        if (box.TryGetCurrentPattern(ValuePattern.Pattern, out object? vp) && vp is ValuePattern valuePattern)
        {
            valuePattern.SetValue(target);
            filled = valuePattern.Current.Value == target;
        }
        Console.WriteLine("输出框已填入恶意路径 = " + filled);
        if (!filled)
        {
            Console.WriteLine("当前值：" + (box.TryGetCurrentPattern(ValuePattern.Pattern, out object? v2) &&
                                            v2 is ValuePattern vp2 ? vp2.Current.Value : "?"));
        }
        Thread.Sleep(600);

        // 4) 点击两个输出操作
        Console.WriteLine();
        Console.WriteLine("点击「打开输出目录」：" + (ClickByMouse(window, "打开输出目录") ? "已点击" : "不可点击"));
        Thread.Sleep(2500);
        Console.WriteLine("点击「播放成片」：" + (ClickByMouse(window, "播放成片") ? "已点击" : "不可点击"));
        Thread.Sleep(3000);

        // 5) 判定：注入生效会拉起计算器
        Process[] calc = Process.GetProcessesByName("CalculatorApp");
        Process[] calcLegacy = Process.GetProcessesByName("calc");
        int injected = calc.Length + calcLegacy.Length;

        Console.WriteLine();
        Console.WriteLine($"注入检出：CalculatorApp={calc.Length}，calc={calcLegacy.Length}");
        Console.WriteLine(injected == 0
            ? "PASS 未发生命令注入"
            : "FAIL 命令注入生效，计算器被拉起");

        foreach (Process c in calc) c.Dispose();
        foreach (Process c in calcLegacy) c.Dispose();

        if (!keep)
        {
            try { File.Delete(target); Directory.Delete(dir, true); } catch { /* 被占用时忽略 */ }
        }

        proc.Dispose();
        return injected == 0 ? 0 : 2;
    }

    private static AutomationElement? FindByAutomationId(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static bool ClickByMouse(AutomationElement? el)
    {
        if (el is null) return false;

        System.Windows.Rect r = el.Current.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0) return false;

        SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
        return true;
    }

    private static bool ClickByMouse(AutomationElement root, string name)
    {
        AutomationElement? el = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
        return ClickByMouse(el);
    }
}
