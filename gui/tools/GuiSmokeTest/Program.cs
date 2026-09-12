using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

/// <summary>
/// 通过 UI Automation 驱动 GUI 做一次真实的短渲染。
///
/// 用法：GuiSmokeTest.exe --exe &lt;AmllMvStudio.exe&gt; --audio &lt;音频&gt;
///       --output &lt;输出.mp4&gt; [--screenshots &lt;目录&gt;] [--timeout-seconds 180]
/// 也可使用 --process-name 连接已运行且已带有效素材启动的实例。
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
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Process? process = null;
        bool ownsProcess = false;
        int exitCode = 0;
        try
        {
            Options options = Options.Parse(args);
            ValidatePrerequisites(options);

            if (File.Exists(options.OutputPath)) File.Delete(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            if (options.ScreenshotDirectory is not null) Directory.CreateDirectory(options.ScreenshotDirectory);

            (process, ownsProcess) = StartOrFindProcess(options);
            AutomationElement window = WaitForWindow(process, WindowTimeout);
            IntPtr hwnd = process.MainWindowHandle;
            ActivateWindow(hwnd);

            Require(window.Current.Name == "AMLL Lyric MV Studio", "窗口标题不符合统一产品名。");
            Console.WriteLine($"窗口 DPI: {GetDpiForWindow(hwnd)} ({GetDpiForWindow(hwnd) / 96.0:P0})");

            SelectNavigationByAutomationId(window, "NavAbout", "AboutVersion", requireDestinationVisible: true);
            AutomationElement aboutVersion = RequireElementByAutomationId(window, "AboutVersion");
            Require(IsVisible(aboutVersion), "关于页版本控件不可见。");
            RequireElementByName(window, "AMLL Lyric MV Studio");

            SelectNavigationByAutomationId(window, "NavExport", "StartRender", requireDestinationVisible: false);
            CaptureIfRequested(hwnd, options, "gui-export-page.png", "导出页");

            RequirePatternByAutomationId(window, "PickAudioButton", InvokePattern.Pattern);
            RequirePatternByAutomationId(window, "PickLyricButton", InvokePattern.Pattern);
            RequirePatternByAutomationId(window, "PickCoverButton", InvokePattern.Pattern);
            RequirePatternByAutomationId(window, "StartRender", InvokePattern.Pattern);
            RequirePatternByAutomationId(window, "CancelRender", InvokePattern.Pattern);
            RequirePatternByAutomationId(window, "HeadDuration", RangeValuePattern.Pattern);
            RequirePatternByAutomationId(window, "TailDuration", RangeValuePattern.Pattern);

            SetRequiredRange(window, "RenderScaleSlider", 0.25);
            SetRequiredRange(window, "QualitySlider", 30);
            WaitForSelection(window, "EncoderCombo", WindowTimeout);
            SetRequiredText(window, "TitleBox", options.Title, automationId: true);
            SetRequiredText(window, "OutputPathBox", options.OutputPath, automationId: true);
            SetRequiredNumber(window, "HeadDuration", 0);
            SetRequiredNumber(window, "TailDuration", 0);

            AutomationElement startButton = ScrollAndRequireByAutomationId(window, "StartRender", WindowTimeout);
            ScrollIntoView(startButton);
            Require(startButton.Current.IsEnabled, "开始渲染按钮未启用。");
            InvokeRequired(startButton, "开始渲染");
            Console.WriteLine("已通过 InvokePattern 调用开始渲染。");

            WaitForSuccessfulRender(window, process, options.OutputPath, TimeSpan.FromSeconds(options.TimeoutSeconds));
            CaptureIfRequested(hwnd, options, "gui-render-complete.png", "渲染完成");
            Console.WriteLine($"PASS: 已生成非空输出文件 {options.OutputPath} ({new FileInfo(options.OutputPath).Length} bytes)");
            exitCode = 0;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine("FAIL: 测试被中断。" + ex.Message);
            exitCode = 4;
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            exitCode = 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            exitCode = 2;
        }
        finally
        {
            if (process is not null)
            {
                if (ownsProcess)
                {
                    try { StopOwnedProcessTree(process); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("FAIL: 清理 GUI 进程失败：" + ex.Message);
                        exitCode = exitCode == 0 ? 5 : exitCode;
                    }
                }
                process.Dispose();
            }
        }
        return exitCode;
    }

    private static void StopOwnedProcessTree(Process process)
    {
        int rootProcessId = process.Id;
        HashSet<int> ownedProcessIds = GetProcessTreeIds(rootProcessId);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(10000))
                throw new TimeoutException($"GUI 根进程 {rootProcessId} 未在 10 秒内退出。");
        }

        WaitForProcessTreeExit(rootProcessId, ownedProcessIds, TimeSpan.FromSeconds(10));
    }

    private static void WaitForProcessTreeExit(int rootProcessId, HashSet<int> knownProcessIds, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int[] survivors;
        do
        {
            knownProcessIds.UnionWith(GetProcessTreeIds(rootProcessId));
            survivors = knownProcessIds.Where(IsProcessAlive).ToArray();
            if (survivors.Length == 0) return;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException("GUI 进程树仍有存活进程：" + string.Join(",", survivors));
    }

    private static HashSet<int> GetProcessTreeIds(int rootProcessId)
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建进程快照。");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExecutableFile = string.Empty };
            if (!Process32First(snapshot, ref entry))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取进程快照。");
            do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }

        var result = new HashSet<int> { rootProcessId };
        bool added;
        do
        {
            added = false;
            foreach ((int processId, int parentId) in parents)
                if (result.Contains(parentId) && result.Add(processId)) added = true;
        } while (added);
        return result;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process candidate = Process.GetProcessById(processId);
            return !candidate.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void ValidatePrerequisites(Options options)
    {
        Require(File.Exists(options.AudioPath), $"音频文件不存在：{options.AudioPath}");
        Require(new FileInfo(options.AudioPath).Length > 0, $"音频文件为空：{options.AudioPath}");
        Require(!string.IsNullOrWhiteSpace(options.Title), "歌曲标题不能为空。");
        Require(Path.IsPathFullyQualified(options.OutputPath), "输出路径必须是绝对路径。");
        Require(string.Equals(Path.GetExtension(options.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase),
            "输出文件必须使用 .mp4 扩展名。");
        if (options.ExecutablePath is not null) Require(File.Exists(options.ExecutablePath), $"目标程序不存在：{options.ExecutablePath}");
    }

    private static (Process Process, bool OwnsProcess) StartOrFindProcess(Options options)
    {
        if (options.ExecutablePath is not null)
        {
            // 启动目标与命令行输入完全解耦：按 staging 固定布局从本工具自身
            // 位置解析 <包根>/AmllMvStudio.exe；--exe 参数仅做一致性校验。
            string packageRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            string discovered = Path.Combine(packageRoot, "AmllMvStudio.exe");
            Require(File.Exists(discovered), $"被测包根目录下未发现 AmllMvStudio.exe：{discovered}");
            Require(string.Equals(Path.GetFullPath(options.ExecutablePath), discovered, StringComparison.OrdinalIgnoreCase),
                $"--exe 与被测包内应用不一致：{options.ExecutablePath} / {discovered}");
            options.ExecutablePath = discovered;
            var start = new ProcessStartInfo(discovered)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!
            };
            start.ArgumentList.Add("--audio");
            start.ArgumentList.Add(options.AudioPath);
            start.ArgumentList.Add("--preset");
            start.ArgumentList.Add("1080p");
            start.ArgumentList.Add("--fps");
            start.ArgumentList.Add("24");
            Process started = new Process { StartInfo = start };
            started.Start();
            return (started, true);
        }

        Process? existing = Process.GetProcessesByName(options.ProcessName)
            .FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero);
        return (existing ?? throw new InvalidOperationException($"找不到目标 GUI 进程：{options.ProcessName}"), false);
    }

    private static AutomationElement WaitForWindow(Process process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (process.HasExited) throw new InvalidOperationException($"GUI 在创建窗口前退出，退出码 {process.ExitCode}。");
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                AutomationElement? window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (window is not null) return window;
            }
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"GUI 未在 {timeout.TotalSeconds:0} 秒内创建主窗口。");
    }

    private static void ActivateWindow(IntPtr hwnd)
    {
        Require(hwnd != IntPtr.Zero, "GUI 主窗口句柄无效。");
        ShowWindow(hwnd, SW_RESTORE);
        for (int i = 0; i < 20 && GetForegroundWindow() != hwnd; i++)
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(100);
        }
    }

    private static void SelectNavigation(
        AutomationElement window,
        string name,
        string destinationAutomationId,
        bool requireDestinationVisible)
    {
        AutomationElement navItem = RequireElementByName(window, name);
        if (!navItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern) ||
            pattern is not SelectionItemPattern selection)
            throw new InvalidOperationException($"导航项不支持 SelectionItemPattern：{name}");
        selection.Select();
        WaitUntil(() =>
        {
            AutomationElement? destination = FindByAutomationId(window, destinationAutomationId);
            return destination is not null && (!requireDestinationVisible || IsVisible(destination));
        }, WindowTimeout, $"导航到“{name}”超时。");
        Require(selection.Current.IsSelected, $"导航项选择状态无效：{name}");
    }

    /// <summary>按 AutomationId 选择导航项：导航项名称会随界面语言变化，AutomationId 才是稳定契约。</summary>
    private static void SelectNavigationByAutomationId(
        AutomationElement window,
        string automationId,
        string destinationAutomationId,
        bool requireDestinationVisible)
    {
        AutomationElement navItem = RequireElementByAutomationId(window, automationId);
        if (!navItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern) ||
            pattern is not SelectionItemPattern selection)
            throw new InvalidOperationException($"导航项不支持 SelectionItemPattern：{automationId}");
        selection.Select();
        WaitUntil(() =>
        {
            AutomationElement? destination = FindByAutomationId(window, destinationAutomationId);
            return destination is not null && (!requireDestinationVisible || IsVisible(destination));
        }, WindowTimeout, $"导航到导航项 {automationId} 超时。");
        Require(selection.Current.IsSelected, $"导航项选择状态无效：{automationId}");
    }

    private static void SetRequiredText(AutomationElement window, string locator, string value, bool automationId = false)
    {
        AutomationElement element = automationId
            ? RequireElementByAutomationId(window, locator)
            : RequireElementByName(window, locator);
        ScrollIntoView(element);
        if (!element.Current.IsEnabled) throw new InvalidOperationException($"输入控件未启用：{locator}");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) || pattern is not ValuePattern input)
            throw new InvalidOperationException($"输入控件不支持 ValuePattern：{locator}");
        input.SetValue(value);
        WaitUntil(() => string.Equals(input.Current.Value, value, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5), $"输入控件未接受目标值：{locator}");
    }

    private static void SetRequiredNumber(AutomationElement window, string automationId, double value)
    {
        AutomationElement element = RequireElementByAutomationId(window, automationId);
        ScrollIntoView(element);
        if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? pattern) ||
            pattern is not RangeValuePattern input)
            throw new InvalidOperationException($"数值控件不支持 RangeValuePattern：{automationId}");
        Require(value >= input.Current.Minimum && value <= input.Current.Maximum,
            $"数值控件目标值越界：{automationId}={value}");
        input.SetValue(value);
        WaitUntil(() => Math.Abs(input.Current.Value - value) < 0.001,
            TimeSpan.FromSeconds(5), $"数值控件未接受目标值：{automationId}");
    }

    private static void WaitForSelection(AutomationElement window, string automationId, TimeSpan timeout)
    {
        AutomationElement combo = RequireElementByAutomationId(window, automationId);
        WaitUntil(() =>
        {
            return combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object? pattern) &&
                pattern is SelectionPattern selection && selection.Current.GetSelection().Length > 0;
        }, timeout, $"控件未在限定时间内产生有效选择：{automationId}");
    }

    private static void SetRequiredRange(AutomationElement window, string automationId, double value)
    {
        AutomationElement element = RequireElementByAutomationId(window, automationId);
        ScrollIntoView(element);
        if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? pattern) ||
            pattern is not RangeValuePattern range)
            throw new InvalidOperationException($"范围控件不支持 RangeValuePattern：{automationId}");
        Require(value >= range.Current.Minimum && value <= range.Current.Maximum,
            $"范围控件目标值越界：{automationId}={value}");
        range.SetValue(value);
        WaitUntil(() => Math.Abs(range.Current.Value - value) < 0.001,
            TimeSpan.FromSeconds(5), $"范围控件未接受目标值：{automationId}");
    }

    private static void ScrollIntoView(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? scrollPattern) &&
            scrollPattern is ScrollItemPattern scrollItem)
        {
            scrollItem.ScrollIntoView();
            Thread.Sleep(250);
            if (!IsVisible(element)) scrollItem.ScrollIntoView();
        }
        WaitUntil(() => IsVisible(element), TimeSpan.FromSeconds(10), $"控件无法滚动到可见区域：{element.Current.Name}");
    }

    private static void InvokeRequired(AutomationElement element, string label)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern) || pattern is not InvokePattern invoke)
            throw new InvalidOperationException($"控件不支持 InvokePattern：{label}");
        try { invoke.Invoke(); }
        catch (ElementNotEnabledException ex) { throw new InvalidOperationException($"控件未启用：{label}", ex); }
    }

    private static void WaitForSuccessfulRender(AutomationElement window, Process process, string outputPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool renderStarted = false;
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
                throw new InvalidOperationException($"GUI 在渲染等待期间退出，退出码 {process.ExitCode}。");

            string status;
            double progress;
            bool startButtonDisabled;
            try
            {
                status = ReadRequiredStatus(window);
                progress = ReadRequiredProgress(window);
                startButtonDisabled = !RequireElementByAutomationId(window, "StartRender").Current.IsEnabled;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // InvokePattern.Invoke 会触发 WinUI 重建自动化树；单次读取失败按未就绪重试，避免瞬态 false-fail。
                Thread.Sleep(500);
                continue;
            }

            if (!string.Equals(status, lastStatus, StringComparison.Ordinal))
            {
                Console.WriteLine($"状态：{status}（{progress:0}%）");
                lastStatus = status;
            }

            if (status.Contains("失败", StringComparison.Ordinal) ||
                status.Contains("中断", StringComparison.Ordinal) ||
                status.Contains("取消", StringComparison.Ordinal) ||
                status.Contains("启动失败", StringComparison.Ordinal))
                throw new InvalidOperationException($"渲染未成功：{status}");

            if (status.Contains("正在", StringComparison.Ordinal) || status.Contains("素材已就绪", StringComparison.Ordinal) ||
                progress > 0 || startButtonDisabled)
                renderStarted = true;

            if (status.Contains("渲染完成", StringComparison.Ordinal))
            {
                Require(renderStarted, "界面报告完成，但未观察到渲染启动。");
                Require(progress >= 100, $"界面报告完成，但进度仅为 {progress:0}%。");
                Require(File.Exists(outputPath), $"渲染完成但输出文件不存在：{outputPath}");
                Require(new FileInfo(outputPath).Length > 0, $"渲染完成但输出文件为空：{outputPath}");
                return;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"渲染未在 {timeout.TotalSeconds:0} 秒内成功完成（最后状态：{lastStatus}）。");
    }

    private static string ReadRequiredStatus(AutomationElement root)
    {
        AutomationElement status = RequireElementByAutomationId(root, "RenderStatus");
        string value = status.Current.Name;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("渲染状态控件值为空。");
        return value;
    }

    private static double ReadRequiredProgress(AutomationElement root)
    {
        AutomationElement progress = RequireElementByAutomationId(root, "RenderProgress");
        if (!progress.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? pattern) || pattern is not RangeValuePattern range)
            throw new InvalidOperationException("渲染进度控件不支持 RangeValuePattern。");
        double value = range.Current.Value;
        if (double.IsNaN(value) || value < range.Current.Minimum || value > range.Current.Maximum)
            throw new InvalidOperationException($"渲染进度值无效：{value}");
        return value;
    }

    private static AutomationElement ScrollAndRequireByAutomationId(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            AutomationElement? element = FindByAutomationId(root, automationId);
            if (element is not null)
            {
                ScrollIntoView(element);
                return element;
            }
            ScrollRoot(root, ScrollAmount.LargeIncrement);
            Thread.Sleep(150);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"滚动后仍缺少控件 AutomationId={automationId}");
    }

    private static void ScrollRoot(AutomationElement root, ScrollAmount verticalAmount)
    {
        AutomationElementCollection scrollables = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        foreach (AutomationElement candidate in scrollables)
        {
            if (!candidate.TryGetCurrentPattern(ScrollPattern.Pattern, out object? pattern) ||
                pattern is not ScrollPattern scroll || !scroll.Current.VerticallyScrollable)
                continue;
            try { scroll.Scroll(ScrollAmount.NoAmount, verticalAmount); }
            catch (InvalidOperationException) { }
        }
    }

    private static AutomationElement RequirePatternByName(AutomationElement root, string name, AutomationPattern pattern)
    {
        AutomationElement element = RequireElementByName(root, name);
        Require(element.TryGetCurrentPattern(pattern, out _), $"控件模式无效：{name} 不支持 {pattern.ProgrammaticName}");
        return element;
    }

    private static AutomationElement RequirePatternByAutomationId(AutomationElement root, string id, AutomationPattern pattern)
    {
        AutomationElement element = RequireElementByAutomationId(root, id);
        Require(element.TryGetCurrentPattern(pattern, out _), $"控件模式无效：{id} 不支持 {pattern.ProgrammaticName}");
        return element;
    }

    private static AutomationElement RequireElementByName(AutomationElement root, string name) =>
        FindByName(root, name) ?? throw new InvalidOperationException($"缺少控件：{name}");

    private static AutomationElement RequireElementByAutomationId(AutomationElement root, string id) =>
        FindByAutomationId(root, id) ?? throw new InvalidOperationException($"缺少控件 AutomationId={id}");

    private static AutomationElement? FindByName(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static AutomationElement? FindByAutomationId(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static bool IsVisible(AutomationElement element)
    {
        System.Windows.Rect bounds = element.Current.BoundingRectangle;
        return !element.Current.IsOffscreen && bounds.Width > 0 && bounds.Height > 0;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            try { if (condition()) return; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(message);
    }

    private static void CaptureIfRequested(IntPtr hwnd, Options options, string fileName, string label)
    {
        if (options.ScreenshotDirectory is null) return;
        string path = Path.Combine(options.ScreenshotDirectory, fileName);
        int width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int height = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        using var bitmap = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"截图（{label}）-> {path}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Options
    {
        public string? ExecutablePath { get; internal set; }
        public string ProcessName { get; private set; } = "AmllMvStudio";
        public string AudioPath { get; private set; } = "";
        public string OutputPath { get; private set; } = "";
        public string Title { get; private set; } = "GUI functional smoke test";
        public string? ScreenshotDirectory { get; private set; }
        public int TimeoutSeconds { get; private set; } = 180;

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    throw new ArgumentException($"无效参数：{key}");
                values[key] = args[++i];
            }

            var options = new Options();
            if (values.TryGetValue("--exe", out string? executable)) options.ExecutablePath = Path.GetFullPath(executable);
            if (values.TryGetValue("--process-name", out string? processName)) options.ProcessName = processName;
            if (values.TryGetValue("--audio", out string? audio)) options.AudioPath = Path.GetFullPath(audio);
            if (values.TryGetValue("--output", out string? output)) options.OutputPath = Path.GetFullPath(output);
            if (values.TryGetValue("--title", out string? title)) options.Title = title;
            if (values.TryGetValue("--screenshots", out string? screenshots)) options.ScreenshotDirectory = Path.GetFullPath(screenshots);
            if (values.TryGetValue("--timeout-seconds", out string? timeout))
            {
                if (!int.TryParse(timeout, out int parsed) || parsed < 1)
                    throw new ArgumentException("--timeout-seconds 必须是正整数。");
                options.TimeoutSeconds = parsed;
            }
            if (options.ExecutablePath is null && !values.ContainsKey("--process-name"))
                throw new ArgumentException("必须提供 --exe 或 --process-name。");
            if (!values.ContainsKey("--audio")) throw new ArgumentException("必须提供 --audio。");
            if (!values.ContainsKey("--output")) throw new ArgumentException("必须提供 --output。");
            return options;
        }
    }
}
