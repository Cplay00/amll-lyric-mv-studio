using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

/// <summary>
/// 完成一次真实短渲染后，验证恶意但合法的输出文件名可安全用于两个输出操作。
///
/// 用法：OutputActionTest.exe --exe &lt;AmllMvStudio.exe&gt; --audio &lt;短音频&gt;
///       --output-dir &lt;目录&gt; [--timeout-seconds 180]
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
    private const string PipeEnvironmentVariable = "AMLL_OUTPUT_ACTION_TEST_PIPE";
    private const string TokenEnvironmentVariable = "AMLL_OUTPUT_ACTION_TEST_TOKEN";
    private static readonly IntPtr InvalidHandleValue = new(-1);

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

    private sealed record LaunchRecord(string Token, string Action, string FullPath);

    [STAThread]
    private static int Main(string[] args)
    {
        Process? gui = null;
        string? outputPath = null;
        int exitCode = 0;
        try
        {
            Options options = Options.Parse(args);
            ValidatePrerequisites(options);

            Directory.CreateDirectory(options.OutputDirectory);
            outputPath = Path.Combine(options.OutputDirectory, "成片 & calc & 结束 ^ (v1) ! 安全测试.mp4");
            if (File.Exists(outputPath)) File.Delete(outputPath);

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string pipeName = "amll-output-action-" + Guid.NewGuid().ToString("N");
            gui = StartGui(options, pipeName, token);
            AutomationElement window = WaitForWindow(gui, TimeSpan.FromSeconds(30));
            ShowWindow(gui.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(gui.MainWindowHandle);

            SelectNavigation(window, "导出成片", "StartRender", requireDestinationVisible: false);
            SetRequiredRange(window, "RenderScaleSlider", 0.25);
            SetRequiredRange(window, "QualitySlider", 30);
            WaitForSelection(window, "EncoderCombo", TimeSpan.FromSeconds(30));
            SetRequiredText(window, "TitleBox", "Output action hostile filename test", true);
            SetRequiredText(window, "OutputPathBox", outputPath, true);
            SetRequiredNumber(window, "HeadDuration", 0);
            SetRequiredNumber(window, "TailDuration", 0);

            AutomationElement startButton = ScrollAndRequireByAutomationId(window, "StartRender", TimeSpan.FromSeconds(30));
            ScrollIntoView(startButton);
            Require(startButton.Current.IsEnabled, "开始渲染按钮未启用。");
            InvokeRequired(startButton, "开始渲染");
            Console.WriteLine("已启动真实短渲染。");

            WaitForSuccessfulRender(window, gui, outputPath, TimeSpan.FromSeconds(options.TimeoutSeconds));
            long outputSize = new FileInfo(outputPath).Length;
            Console.WriteLine($"渲染完成：{outputPath} ({outputSize} bytes)");

            AutomationElement revealButton = ScrollAndRequireByAutomationId(window, "OpenOutputButton", TimeSpan.FromSeconds(30));
            Require(revealButton.TryGetCurrentPattern(InvokePattern.Pattern, out _),
                "控件模式无效：OpenOutputButton 不支持 InvokePattern。");
            Require(revealButton.Current.IsEnabled, "渲染成功后“定位输出”按钮未启用。");
            using var revealPipe = CreatePipeServer(pipeName);
            LaunchRecord reveal = InvokeAndReadLaunchRecord(
                revealButton, "定位输出", revealPipe, TimeSpan.FromSeconds(10));
            ValidateLaunchRecord(reveal, token, "reveal", outputPath);
            Console.WriteLine("已验证“定位输出”处理程序记录 reveal 与规范完整路径。");

            AutomationElement playButton = ScrollAndRequireByAutomationId(window, "PlayResultButton", TimeSpan.FromSeconds(30));
            Require(playButton.TryGetCurrentPattern(InvokePattern.Pattern, out _),
                "控件模式无效：PlayResultButton 不支持 InvokePattern。");
            Require(playButton.Current.IsEnabled, "渲染成功后“播放成片”按钮未启用。");
            using var playPipe = CreatePipeServer(pipeName);
            LaunchRecord play = InvokeAndReadLaunchRecord(
                playButton, "播放成片", playPipe, TimeSpan.FromSeconds(10));
            ValidateLaunchRecord(play, token, "play", outputPath);
            Console.WriteLine("已验证“播放成片”处理程序记录 play 与规范完整路径。");

            Require(File.Exists(outputPath) && new FileInfo(outputPath).Length == outputSize && outputSize > 0,
                "调用输出操作后，渲染文件缺失、为空或被修改。");

            StopGui(gui);
            gui.Dispose();
            gui = null;
            DeleteOutputWithRetry(outputPath);
            Require(!File.Exists(outputPath), "无法清理测试输出文件。");

            Console.WriteLine("PASS: 两个真实 UIA Invoke 均经生产处理程序产生正确契约记录，且未启动外部程序。");
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
            if (gui is not null)
            {
                try { StopGui(gui); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FAIL: 清理 GUI 进程失败：" + ex.Message);
                    exitCode = exitCode == 0 ? 5 : exitCode;
                }
                gui.Dispose();
            }
            if (outputPath is not null)
            {
                try { DeleteOutputWithRetry(outputPath); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FAIL: 清理输出文件失败：" + ex.Message);
                    exitCode = exitCode == 0 ? 5 : exitCode;
                }
            }
        }
        return exitCode;
    }

    private static void StopGui(Process gui)
    {
        int rootProcessId = gui.Id;
        HashSet<int> ownedProcessIds = GetProcessTreeIds(rootProcessId);
        if (!gui.HasExited)
        {
            gui.Kill(entireProcessTree: true);
            if (!gui.WaitForExit(10000)) throw new TimeoutException("GUI 根进程未在 10 秒内退出。");
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

    private static NamedPipeServerStream CreatePipeServer(string pipeName) => new(
        pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static LaunchRecord InvokeAndReadLaunchRecord(
        AutomationElement button,
        string label,
        NamedPipeServerStream pipe,
        TimeSpan timeout)
    {
        Task<LaunchRecord> readTask = Task.Run(() => ReadLaunchRecord(pipe, timeout));
        InvokeRequired(button, label);
        try { return readTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new TimeoutException($"{label}未连接受控测试管道。", ex); }
    }

    private static LaunchRecord ReadLaunchRecord(NamedPipeServerStream pipe, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { pipe.WaitForConnectionAsync(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new TimeoutException("输出操作未连接受控测试管道。", ex); }
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: false);
        return new LaunchRecord(reader.ReadString(), reader.ReadString(), reader.ReadString());
    }

    private static void ValidateLaunchRecord(LaunchRecord record, string token, string action, string expectedPath)
    {
        Require(string.Equals(record.Token, token, StringComparison.Ordinal), "输出操作测试 token 不匹配。");
        Require(string.Equals(record.Action, action, StringComparison.Ordinal),
            $"输出操作类型错误：期望 {action}，实际 {record.Action}。");
        Require(Path.IsPathFullyQualified(record.FullPath), "输出操作记录的路径不是完整路径。");
        Require(string.Equals(record.FullPath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase),
            $"输出操作路径错误：{record.FullPath}");
        Require(Path.GetFileName(record.FullPath) == "成片 & calc & 结束 ^ (v1) ! 安全测试.mp4",
            "敌意合法文件名未被原样传递。");
    }

    private static void DeleteOutputWithRetry(string outputPath)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            if (!File.Exists(outputPath)) return;
            try
            {
                File.Delete(outputPath);
                if (!File.Exists(outputPath)) return;
            }
            catch (IOException ex) { lastError = ex; }
            catch (UnauthorizedAccessException ex) { lastError = ex; }
            Thread.Sleep(attempt * 200);
        }
        throw new IOException($"无法清理输出文件：{outputPath}", lastError);
    }

    private static void ValidatePrerequisites(Options options)
    {
        Require(Environment.GetEnvironmentVariable(PipeEnvironmentVariable) is null &&
            Environment.GetEnvironmentVariable(TokenEnvironmentVariable) is null,
            $"测试宿主环境不得预先定义 {PipeEnvironmentVariable} 或 {TokenEnvironmentVariable}。");
        Require(File.Exists(options.ExecutablePath), $"目标程序不存在：{options.ExecutablePath}");
        Require(File.Exists(options.AudioPath), $"音频文件不存在：{options.AudioPath}");
        Require(new FileInfo(options.AudioPath).Length > 0, $"音频文件为空：{options.AudioPath}");
        Require(Path.IsPathFullyQualified(options.OutputDirectory), "输出目录必须是绝对路径。");
    }

    private static Process StartGui(Options options, string pipeName, string token)
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
        start.Environment[PipeEnvironmentVariable] = pipeName;
        start.Environment[TokenEnvironmentVariable] = token;
        start.ArgumentList.Add("--audio");
        start.ArgumentList.Add(options.AudioPath);
        start.ArgumentList.Add("--preset");
        start.ArgumentList.Add("1080p");
        start.ArgumentList.Add("--fps");
        start.ArgumentList.Add("24");
        Process process = new Process { StartInfo = start };
        process.Start();
        return process;
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
                AutomationElement? element = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (element is not null) return element;
            }
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("GUI 未在 30 秒内创建主窗口。");
    }

    private static void SelectNavigation(
        AutomationElement window,
        string name,
        string destinationAutomationId,
        bool requireDestinationVisible)
    {
        AutomationElement nav = RequireByName(window, name);
        if (!nav.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern) ||
            pattern is not SelectionItemPattern selection)
            throw new InvalidOperationException($"导航项不支持 SelectionItemPattern：{name}");
        selection.Select();
        WaitUntil(() =>
        {
            AutomationElement? destination = FindByAutomationId(window, destinationAutomationId);
            return selection.Current.IsSelected && destination is not null &&
                (!requireDestinationVisible || IsVisible(destination));
        }, TimeSpan.FromSeconds(30), $"导航到“{name}”超时。");
        Require(selection.Current.IsSelected, $"导航项未保持选中：{name}");
        Thread.Sleep(500);
    }

    private static void SetRequiredText(AutomationElement window, string locator, string value, bool automationId)
    {
        AutomationElement element = automationId ? RequireByAutomationId(window, locator) : RequireByName(window, locator);
        ScrollIntoView(element);
        SetValue(element, locator, value);
        WaitUntil(() =>
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) &&
                pattern is ValuePattern input && string.Equals(input.Current.Value, value, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5), $"输入控件未接受目标值：{locator}");
    }

    private static void SetRequiredNumber(AutomationElement window, string automationId, double value)
    {
        AutomationElement element = RequireByAutomationId(window, automationId);
        ScrollIntoView(element);
        Require(element.Current.IsEnabled, $"数值控件未启用：{automationId}");
        if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? pattern) ||
            pattern is not RangeValuePattern range)
            throw new InvalidOperationException($"数值控件不支持 RangeValuePattern：{automationId}");
        Require(value >= range.Current.Minimum && value <= range.Current.Maximum,
            $"数值控件目标值越界：{automationId}={value}");
        range.SetValue(value);
        WaitUntil(() => Math.Abs(range.Current.Value - value) < 0.001,
            TimeSpan.FromSeconds(5), $"数值控件未接受目标值：{automationId}");
    }

    private static void SetValue(AutomationElement element, string label, string value)
    {
        Require(element.Current.IsEnabled, $"输入控件未启用：{label}");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) || pattern is not ValuePattern input)
            throw new InvalidOperationException($"输入控件不支持 ValuePattern：{label}");
        input.SetValue(value);
    }

    private static void WaitForSelection(AutomationElement window, string automationId, TimeSpan timeout)
    {
        AutomationElement combo = RequireByAutomationId(window, automationId);
        WaitUntil(() =>
        {
            return combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object? pattern) &&
                pattern is SelectionPattern selection && selection.Current.GetSelection().Length > 0;
        }, timeout, $"控件未在限定时间内产生有效选择：{automationId}");
    }

    private static void SetRequiredRange(AutomationElement window, string automationId, double value)
    {
        AutomationElement element = RequireByAutomationId(window, automationId);
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
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? pattern) && pattern is ScrollItemPattern scroll)
        {
            scroll.ScrollIntoView();
            Thread.Sleep(250);
            if (!IsVisible(element)) scroll.ScrollIntoView();
        }
        WaitUntil(() => IsVisible(element), TimeSpan.FromSeconds(10), $"控件无法滚动到可见区域：{element.Current.Name}");
    }

    private static void InvokeRequired(AutomationElement element, string label)
    {
        ScrollIntoView(element);
        Require(element.Current.IsEnabled, $"控件未启用：{label}");
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern) || pattern is not InvokePattern invoke)
            throw new InvalidOperationException($"控件不支持 InvokePattern：{label}");
        invoke.Invoke();
    }

    private static void WaitForSuccessfulRender(AutomationElement window, Process gui, string outputPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool started = false;
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            gui.Refresh();
            if (gui.HasExited)
                throw new InvalidOperationException($"GUI 在渲染等待期间退出，退出码 {gui.ExitCode}。");

            string status;
            double progress;
            bool startButtonDisabled;
            try
            {
                AutomationElement statusElement = RequireByAutomationId(window, "RenderStatus");
                status = statusElement.Current.Name;
                if (string.IsNullOrWhiteSpace(status)) throw new InvalidOperationException("渲染状态为空。");
                progress = ReadRequiredProgress(window);
                startButtonDisabled = !RequireByAutomationId(window, "StartRender").Current.IsEnabled;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // InvokePattern.Invoke 会触发 WinUI 重建自动化树；单次读取失败按未就绪重试，避免瞬态 false-fail。
                Thread.Sleep(500);
                continue;
            }

            if (!string.Equals(lastStatus, status, StringComparison.Ordinal))
            {
                Console.WriteLine($"状态：{status}（{progress:0}%）");
                lastStatus = status;
            }
            if (status.Contains("失败", StringComparison.Ordinal) || status.Contains("中断", StringComparison.Ordinal) ||
                status.Contains("取消", StringComparison.Ordinal) || status.Contains("启动失败", StringComparison.Ordinal))
                throw new InvalidOperationException($"渲染未成功：{status}");
            if (status.Contains("正在", StringComparison.Ordinal) || status.Contains("素材已就绪", StringComparison.Ordinal) ||
                progress > 0 || startButtonDisabled)
                started = true;
            if (status.Contains("渲染完成", StringComparison.Ordinal))
            {
                Require(started, "界面报告完成，但未观察到渲染启动。");
                Require(progress >= 100, $"界面报告完成，但进度仅为 {progress:0}%。");
                Require(File.Exists(outputPath), $"渲染完成但输出文件不存在：{outputPath}");
                Require(new FileInfo(outputPath).Length > 0, $"渲染完成但输出文件为空：{outputPath}");
                return;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"渲染未在 {timeout.TotalSeconds:0} 秒内成功完成（最后状态：{lastStatus}）。");
    }

    private static double ReadRequiredProgress(AutomationElement window)
    {
        AutomationElement element = RequireByAutomationId(window, "RenderProgress");
        if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? pattern) || pattern is not RangeValuePattern range)
            throw new InvalidOperationException("渲染进度控件不支持 RangeValuePattern。");
        double value = range.Current.Value;
        if (double.IsNaN(value) || value < range.Current.Minimum || value > range.Current.Maximum)
            throw new InvalidOperationException($"渲染进度无效：{value}");
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

    private static AutomationElement RequirePatternByAutomationId(
        AutomationElement root,
        string automationId,
        AutomationPattern pattern)
    {
        AutomationElement element = RequireByAutomationId(root, automationId);
        Require(element.TryGetCurrentPattern(pattern, out _),
            $"控件模式无效：{automationId} 不支持 {pattern.ProgrammaticName}");
        return element;
    }

    private static AutomationElement RequireByName(AutomationElement root, string name) =>
        FindByName(root, name) ?? throw new InvalidOperationException($"缺少控件：{name}");

    private static AutomationElement RequireByAutomationId(AutomationElement root, string id) =>
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Options
    {
        public string ExecutablePath { get; internal set; } = "";
        public string AudioPath { get; private set; } = "";
        public string OutputDirectory { get; private set; } = "";
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
            if (!values.TryGetValue("--exe", out string? executable)) throw new ArgumentException("必须提供 --exe。");
            if (!values.TryGetValue("--audio", out string? audio)) throw new ArgumentException("必须提供 --audio。");
            if (!values.TryGetValue("--output-dir", out string? outputDir)) throw new ArgumentException("必须提供 --output-dir。");
            var options = new Options
            {
                ExecutablePath = Path.GetFullPath(executable),
                AudioPath = Path.GetFullPath(audio),
                OutputDirectory = Path.GetFullPath(outputDir)
            };
            if (values.TryGetValue("--timeout-seconds", out string? timeout))
            {
                if (!int.TryParse(timeout, out int parsed) || parsed < 1)
                    throw new ArgumentException("--timeout-seconds 必须是正整数。");
                options.TimeoutSeconds = parsed;
            }
            return options;
        }
    }
}
