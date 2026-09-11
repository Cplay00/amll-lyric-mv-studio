using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

/// <summary>
/// 通过 UI Automation 驱动 GUI 做冒烟测试：切换页面、点击按钮、检查控件、等待渲染结果。
/// 用于在没有人工操作的情况下验证 WinUI 界面确实可用。
///
/// 用法：GuiSmokeTest.exe &lt;进程名&gt; &lt;截图输出目录&gt;
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int SW_RESTORE = 9;

    private static int Main(string[] args)
    {
        string processName = args.Length > 0 ? args[0] : "AmllMvStudio";
        string outDir = args.Length > 1 ? args[1] : ".";

        Process? proc = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

        if (proc is null)
        {
            Console.WriteLine($"NOT_FOUND: {processName}");
            return 1;
        }

        IntPtr hwnd = proc.MainWindowHandle;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        for (int i = 0; i < 25 && GetForegroundWindow() != hwnd; i++)
        {
            Thread.Sleep(200);
            SetForegroundWindow(hwnd);
        }
        Console.WriteLine("前台窗口: " + (GetForegroundWindow() == hwnd ? "OK" : "FAILED"));

        AutomationElement? window = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id));

        if (window is null)
        {
            Console.WriteLine("找不到主窗口的自动化元素");
            return 1;
        }

        Console.WriteLine("窗口标题: " + window.Current.Name);
        DumpButtons(window);

        // 1) 切到「导出成片」：优先鼠标点击（NavigationView 对 Selection 模式的响应不稳定）
        AutomationElement? exportNav = FindByName(window, "导出成片");
        if (exportNav is null)
        {
            Console.WriteLine("找不到「导出成片」导航项");
            return 1;
        }

        ClickByMouse(exportNav);
        Thread.Sleep(800);

        // 等待导出页真正可见（以「开始渲染」按钮出现有效边界为判据）
        AutomationElement? startButton = null;
        for (int i = 0; i < 20; i++)
        {
            startButton = FindByAutomationId(window, "StartRender");
            if (startButton is not null)
            {
                System.Windows.Rect r = startButton.Current.BoundingRectangle;
                if (r.Width > 0 && r.Height > 0)
                {
                    Console.WriteLine($"导出页已显示（第 {i} 次检查）");
                    break;
                }
            }
            ClickByMouse(exportNav);
            Thread.Sleep(700);
        }

        Capture(hwnd, Path.Combine(outDir, "gui-export-page.png"), "导出页");

        // 2) 检查导出页关键控件
        string[] expected = ["选择音频（必填）", "选择歌词", "选择封面", "开始渲染", "取消"];
        foreach (string name in expected)
        {
            Console.WriteLine($"{(FindByName(window, name) is not null ? "OK  " : "MISS")} 控件：{name}");
        }

        // 3) 点击「开始渲染」并跟踪状态文本与进度
        if (startButton is null)
        {
            Console.WriteLine("找不到「开始渲染」按钮");
            proc.Dispose();
            return 1;
        }

        bool clicked = ClickByMouse(startButton);
        Console.WriteLine(clicked ? "已点击「开始渲染」，等待进度…" : "点击失败（控件无有效边界）");

        if (clicked)
        {
            string last = "";
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(2500);
                string text = ReadStatusText(window);
                double pct = ReadProgress(window);
                string line = $"{text}（进度 {pct:0}%）";
                if (line != last)
                {
                    Console.WriteLine($"  [{i}] {line}");
                    last = line;
                }
                if (text.Contains("渲染完成") || text.Contains("失败") || text.Contains("中断")) break;
            }
            Thread.Sleep(800);
            Capture(hwnd, Path.Combine(outDir, "gui-rendering.png"), "渲染结果");
        }

        proc.Dispose();
        return 0;
    }

    private static void Capture(IntPtr hwnd, string path, string label)
    {
        int w = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int h = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

        using var bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
        }
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"截图（{label}）-> {path}");
    }

    private static void DumpButtons(AutomationElement root)
    {
        AutomationElementCollection all = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        Console.WriteLine($"共 {all.Count} 个按钮");
        foreach (AutomationElement el in all)
        {
            System.Windows.Rect r = el.Current.BoundingRectangle;
            bool hasInvoke = el.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            string pattern = hasInvoke ? "Invoke" : "-";
            Console.WriteLine(
                $"  - 名称[{el.Current.Name}] 边界({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0}) 模式={pattern}");
        }
    }

    private static AutomationElement? FindByName(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static AutomationElement? FindByAutomationId(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    /// <summary>读取导出页状态文本（通过 AutomationId 精确定位）</summary>
    private static string ReadStatusText(AutomationElement root)
    {
        AutomationElement? el = FindByAutomationId(root, "RenderStatus");
        if (el is not null) return el.Current.Name;

        // 回退：取第一个非空文本
        AutomationElementCollection texts = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement t in texts)
        {
            string s = t.Current.Name;
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return "";
    }

    /// <summary>读取进度条当前值</summary>
    private static double ReadProgress(AutomationElement root)
    {
        AutomationElement? el = FindByAutomationId(root, "RenderProgress");
        if (el is null) return -1;
        if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? p) && p is RangeValuePattern rv)
            return rv.Current.Value;
        return -1;
    }

    private static bool ClickByName(AutomationElement root, string name)
    {
        AutomationElement? el = FindByName(root, name);
        if (el is null) return false;

        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern) &&
            pattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return true;
        }

        // NavigationViewItem 等列表型控件通过 SelectionItem 激活
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? sel) &&
            sel is SelectionItemPattern selection)
        {
            selection.Select();
            return true;
        }

        // 内容由子元素构成的按钮拿不到 InvokePattern，退回鼠标点击中心点
        return ClickByMouse(el);
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static bool ClickByMouse(AutomationElement el)
    {
        System.Windows.Rect r = el.Current.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0) return false;

        int cx = (int)(r.X + r.Width / 2);
        int cy = (int)(r.Y + r.Height / 2);

        SetCursorPos(cx, cy);
        Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(120);
        return true;
    }
}
