using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AmllMvStudio.Services;

public sealed class RenderProgressEventArgs : EventArgs
{
    public string Stage { get; init; } = "";
    public int Percent { get; init; }
    public int Frames { get; init; }
    public int LyricLines { get; init; }
    public long ElapsedMs { get; init; }
    public long TotalMs { get; init; }
    public string? OutputPath { get; init; }
    public long Bytes { get; init; }
    public string? Error { get; init; }
}

public sealed class RenderRequest
{
    public required string AudioPath { get; init; }
    public string? LyricPath { get; init; }
    public string? CoverPath { get; init; }
    public string? OutputPath { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public double FontScale { get; init; } = 1;
    public int FontWeight { get; init; } = 600;
    public string SecondaryMode { get; init; } = RenderContracts.DefaultSecondaryMode;
    public bool ShowSongInfo { get; init; } = true;
    public bool ShowArtist { get; init; } = true;
    public bool ShowAlbum { get; init; } = true;
    public bool ShowArtwork { get; init; } = true;
    public bool ShowControls { get; init; }
    public double ArtworkScale { get; init; } = 1;
    public string LyricAlign { get; init; } = "left";
    public double LyricOffset { get; init; }
    public double LyricWidth { get; init; } = RenderContracts.DefaultLyricWidth;
    public double LyricFade { get; init; } = RenderContracts.DefaultLyricFade;
    public double BackgroundDim { get; init; } = 0.35;
    public double BackgroundSaturation { get; init; } = RenderContracts.DefaultBackgroundSaturation;
    public double BackgroundBlur { get; init; } = 24;
    public string Preset { get; init; } = "1080p";
    public int Fps { get; init; } = 60;
    public double FlowSpeed { get; init; } = 2;
    public double RenderScale { get; init; } = 1;
    public int Head { get; init; }
    public int Tail { get; init; } = 1200;
    public int TailMs { get => Tail; init => Tail = value; }
    public int Crf { get => Quality; init => Quality = value; }
    public string Encoder { get; init; } = RenderContracts.DefaultEncoder;
    public string EncoderPreset { get; init; } = "";
    public int Quality { get; init; } = 16;
    public string AudioCodec { get; init; } = RenderContracts.DefaultAudioCodec;
    public string AudioBitrate { get; init; } = "320k";
    public string BrowserGpu { get; init; } = RenderContracts.DefaultBrowserGpu;
}

public sealed record EncoderProbeResult(
    bool Compiled,
    bool Available,
    string? FailureReason,
    IReadOnlyList<string> SupportedPresets,
    string? DefaultPreset);

public sealed record EncoderOption(string Id, string Label, bool IsAvailable)
{
    public bool IsCompiled { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<string> SupportedPresets { get; init; } = Array.Empty<string>();
    public string? DefaultPreset { get; init; }
    public string DisplayLabel => IsAvailable ? Label : $"{Label}（不可用）";
}

/// <summary>调用 scripts/render-mv.mjs，并将机器可读协议转换为 GUI 事件。</summary>
public sealed class RenderRunner : IDisposable
{
    private static readonly IReadOnlyList<(string Id, string Label)> KnownEncoders =
    [
        ("auto", "自动选择（失败时回退）"),
        ("libx264", "H.264 软件编码 (libx264)"),
        ("libx265", "HEVC 软件编码 (libx265)"),
        ("h264_nvenc", "NVIDIA H.264 (NVENC)"),
        ("hevc_nvenc", "NVIDIA HEVC (NVENC)"),
        ("h264_qsv", "Intel H.264 (Quick Sync)"),
        ("hevc_qsv", "Intel HEVC (Quick Sync)"),
        ("h264_amf", "AMD H.264 (AMF)"),
        ("hevc_amf", "AMD HEVC (AMF)"),
    ];

    private readonly string _projectRoot;
    private readonly object _sync = new();
    private Process? _process;
    private int _generation;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _process is { HasExited: false };
        }
    }

    public event EventHandler<RenderProgressEventArgs>? Progress;
    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? Completed;

    public RenderRunner(string projectRoot) => _projectRoot = projectRoot;

    public static string? FindNodeExecutable() => RuntimePaths.FindNodeExecutable();

    public static IReadOnlyList<string> BuildArguments(
        string scriptPath,
        RenderRequest request,
        string? ffmpegPath = null,
        string? browserPath = null,
        IReadOnlyCollection<string>? supportedPresets = null)
    {
        ValidateRequest(request, supportedPresets);
        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string B(bool value) => value ? "true" : "false";

        var args = new List<string>
        {
            scriptPath,
            "--audio", request.AudioPath,
            "--preset", request.Preset,
            "--fps", request.Fps.ToString(CultureInfo.InvariantCulture),
            "--flow-speed", F(request.FlowSpeed),
            "--render-scale", F(request.RenderScale),
            "--head", request.Head.ToString(CultureInfo.InvariantCulture),
            "--tail", request.Tail.ToString(CultureInfo.InvariantCulture),
            "--title", request.Title,
            "--font-family", request.FontFamily,
            "--font-scale", F(request.FontScale),
            "--font-weight", request.FontWeight.ToString(CultureInfo.InvariantCulture),
            "--secondary-mode", request.SecondaryMode,
            "--show-song-info", B(request.ShowSongInfo),
            "--show-artist", B(request.ShowArtist),
            "--show-album", B(request.ShowAlbum),
            "--show-artwork", B(request.ShowArtwork),
            "--show-controls", B(request.ShowControls),
            "--artwork-scale", F(request.ArtworkScale),
            "--lyric-align", request.LyricAlign,
            "--lyric-offset", F(request.LyricOffset),
            "--lyric-width", F(request.LyricWidth),
            "--lyric-fade", F(request.LyricFade),
            "--background-dim", F(request.BackgroundDim),
            "--background-saturation", F(request.BackgroundSaturation),
            "--background-blur", F(request.BackgroundBlur),
            "--encoder", request.Encoder,
            "--quality", request.Quality.ToString(CultureInfo.InvariantCulture),
            "--audio-codec", request.AudioCodec,
            "--browser-gpu", request.BrowserGpu,
            "--progress-json",
        };

        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new ArgumentException("GUI 渲染请求必须提供绝对输出路径。", nameof(request.OutputPath));
        Add("--out", request.OutputPath);
        if (!string.IsNullOrWhiteSpace(request.Artist)) Add("--artist", request.Artist);
        if (!string.IsNullOrWhiteSpace(request.Album)) Add("--album", request.Album);
        if (request.Encoder != "auto") Add("--encoder-preset", request.EncoderPreset);
        if (request.AudioCodec == "aac") Add("--audio-bitrate", request.AudioBitrate);
        if (!string.IsNullOrWhiteSpace(ffmpegPath)) Add("--ffmpeg", Path.GetFullPath(ffmpegPath));
        if (!string.IsNullOrWhiteSpace(browserPath)) Add("--browser", Path.GetFullPath(browserPath));
        if (!string.IsNullOrWhiteSpace(request.LyricPath)) Add("--lyric", request.LyricPath!);
        if (!string.IsNullOrWhiteSpace(request.CoverPath)) Add("--cover", request.CoverPath!);
        return args;

        void Add(string flag, string value)
        {
            args.Add(flag);
            args.Add(value);
        }
    }

    public static void ValidateRequest(RenderRequest request, IReadOnlyCollection<string>? supportedPresets = null)
    {
        RenderContracts.ValidateChoice(request.SecondaryMode, RenderContracts.SecondaryModes, nameof(request.SecondaryMode));
        RenderContracts.ValidateChoice(request.BrowserGpu, RenderContracts.BrowserGpuModes, nameof(request.BrowserGpu));
        RenderContracts.ValidateChoice(request.AudioCodec, RenderContracts.AudioCodecs, nameof(request.AudioCodec));
        RenderContracts.ValidateChoice(request.Encoder, RenderContracts.Encoders, nameof(request.Encoder));
        RenderContracts.RequireRange(request.LyricWidth, 30, 80, nameof(request.LyricWidth));
        RenderContracts.RequireRange(request.LyricFade, 0, 0.3, nameof(request.LyricFade));
        RenderContracts.RequireRange(request.LyricOffset, -600, 600, nameof(request.LyricOffset));
        RenderContracts.RequireRange(request.BackgroundSaturation, 0, 2, nameof(request.BackgroundSaturation));
        string? audioOutputError = RenderContracts.GetAudioOutputError(request.OutputPath, request.AudioCodec, request.Head, request.Tail);
        if (audioOutputError is not null) throw new ArgumentException(audioOutputError, nameof(request.AudioCodec));
        if (!Path.IsPathFullyQualified(request.OutputPath ?? ""))
            throw new ArgumentException("GUI 渲染请求必须提供绝对输出路径。", nameof(request.OutputPath));
        if (!RenderContracts.IsKnownEncoderPreset(request.Encoder, request.EncoderPreset))
            throw new ArgumentException($"{request.Encoder} 不接受 preset {request.EncoderPreset}。", nameof(request.EncoderPreset));
        if (supportedPresets is not null && request.Encoder != "auto" &&
            !supportedPresets.Contains(request.EncoderPreset, StringComparer.Ordinal))
            throw new ArgumentException($"{request.Encoder} 的探测能力不支持 preset {request.EncoderPreset}。", nameof(request.EncoderPreset));
    }

    public async Task<IReadOnlyList<EncoderOption>> ProbeEncodersAsync(CancellationToken cancellationToken = default)
    {
        string? nodeExe = FindNodeExecutable();
        string scriptPath = Path.Combine(_projectRoot, "scripts", "render-mv.mjs");
        if (nodeExe is null || !File.Exists(scriptPath))
            return MarkUnavailable("找不到 Node.js 或编码器探测脚本。");

        var args = new List<string> { scriptPath, "--list-encoders", "--progress-json" };
        string? ffmpegPath = RuntimePaths.FindFfmpegExecutable(_projectRoot);
        if (ffmpegPath is not null) args.AddRange(["--ffmpeg", ffmpegPath]);
        var psi = CreateProcessStartInfo(nodeExe, args);
        using var process = new Process { StartInfo = psi };
        // 总超时兜底：损坏 GPU 驱动下硬件试编码可能逼近 2 分钟，UI 不应无限等待
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            process.Start();
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitForProbeCleanupAsync(process, stdout, stderr);
                if (cancellationToken.IsCancellationRequested) throw;
                return MarkUnavailable("FFmpeg 编码器探测在 90 秒后超时。");
            }
            string text = (await stdout) + Environment.NewLine + (await stderr);
            var detected = ParseEncoderCapabilities(text);
            if (detected.Count == 0) return MarkUnavailable("编码器探测未返回有效结果。");
            return KnownEncoders.Select(x => detected.TryGetValue(x.Id, out EncoderProbeResult? item)
                ? CreateOption(x.Id, x.Label, item)
                : new EncoderOption(x.Id, x.Label, false)
                {
                    FailureReason = "编码器探测结果缺失。",
                }).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            if (stdout is not null && stderr is not null)
                await WaitForProbeCleanupAsync(process, stdout, stderr);
            Debug.WriteLine($"[RenderRunner] 编码器探测失败：{ex.Message}");
            return MarkUnavailable($"FFmpeg 编码器探测失败：{ex.Message}");
        }

        static async Task WaitForProbeCleanupAsync(Process process, Task<string> stdout, Task<string> stderr)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Debug.WriteLine($"[RenderRunner] 等待探测进程退出失败：{ex.Message}"); }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Debug.WriteLine($"[RenderRunner] 等待探测输出结束失败：{ex.Message}"); }
        }

        static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killEx) { Debug.WriteLine($"[RenderRunner] 探测进程清理失败：{killEx.Message}"); }
        }

        static EncoderOption CreateOption(string id, string label, EncoderProbeResult item)
        {
            IReadOnlyList<string> presets = RenderContracts.GetSupportedEncoderPresets(id, item.SupportedPresets);
            bool available = item.Available && (id == "auto" || presets.Count > 0);
            return new EncoderOption(id, label, available)
            {
                IsCompiled = item.Compiled,
                FailureReason = available ? item.FailureReason : item.FailureReason ?? "未探测到受支持的编码预设。",
                SupportedPresets = presets,
                DefaultPreset = presets.Contains(item.DefaultPreset ?? "", StringComparer.Ordinal)
                    ? item.DefaultPreset
                    : presets.FirstOrDefault(),
            };
        }

        static IReadOnlyList<EncoderOption> MarkUnavailable(string reason) =>
            KnownEncoders.Select(x => new EncoderOption(x.Id, x.Label, false)
            {
                FailureReason = reason,
            }).ToArray();
    }

    internal static Dictionary<string, EncoderProbeResult> ParseEncoderCapabilities(string text)
    {
        var result = new Dictionary<string, EncoderProbeResult>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            try
            {
                using var doc = JsonDocument.Parse(line.StartsWith("@@ENCODERS ", StringComparison.Ordinal)
                    ? line["@@ENCODERS ".Length..]
                    : line);
                JsonElement root = doc.RootElement;
                JsonElement list = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.TryGetProperty("encoders", out JsonElement encoders) ? encoders : default;
                if (list.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string? id = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString()
                        : item.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || !KnownEncoders.Any(x => x.Id == id)) continue;
                    bool available = item.ValueKind == JsonValueKind.String ||
                        !item.TryGetProperty("available", out JsonElement availableElement) ||
                        availableElement.ValueKind == JsonValueKind.True;
                    bool compiled = item.ValueKind == JsonValueKind.String ||
                        !item.TryGetProperty("compiled", out JsonElement compiledElement) ||
                        compiledElement.ValueKind == JsonValueKind.True;
                    string? failureReason = item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("failureReason", out JsonElement reasonElement) &&
                        reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
                    IReadOnlyList<string> presets = ReadPresets(item);
                    string? defaultPreset = item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("defaultPreset", out JsonElement defaultElement) &&
                        defaultElement.ValueKind == JsonValueKind.String ? defaultElement.GetString() : null;
                    result[id] = new EncoderProbeResult(compiled, available, failureReason, presets, defaultPreset);
                }
            }
            catch (JsonException)
            {
                // Ignore logs and legacy text. Availability now requires structured real-probe results.
            }
        }

        if (!result.ContainsKey("auto"))
        {
            bool available = result.TryGetValue("libx264", out EncoderProbeResult? x264) && x264.Available;
            result["auto"] = new EncoderProbeResult(available, available,
                available ? null : "自动编码要求可用的 libx264 CPU 回退。", Array.Empty<string>(), null);
        }
        return result;

        static IReadOnlyList<string> ReadPresets(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            JsonElement values = item.TryGetProperty("supportedPresets", out JsonElement supported)
                ? supported
                : item.TryGetProperty("presets", out JsonElement legacy) ? legacy : default;
            if (values.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }
    }

    internal static Dictionary<string, bool> ParseEncoderAvailability(string text) =>
        ParseEncoderCapabilities(text).ToDictionary(x => x.Key, x => x.Value.Available, StringComparer.OrdinalIgnoreCase);

    public void Start(RenderRequest request, IReadOnlyCollection<string>? supportedPresets = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string scriptPath = Path.Combine(_projectRoot, "scripts", "render-mv.mjs");
        if (!File.Exists(scriptPath)) throw new FileNotFoundException($"找不到渲染脚本：{scriptPath}");
        string nodeExe = FindNodeExecutable() ??
            throw new FileNotFoundException("未找到 Node.js，请先安装 Node.js 后重试。");

        Process process;
        int generation;
        lock (_sync)
        {
            if (_process is { HasExited: false }) throw new InvalidOperationException("已有渲染任务正在运行。");
            _process?.Dispose();
            generation = ++_generation;
            process = new Process
            {
                StartInfo = CreateProcessStartInfo(nodeExe, BuildArguments(
                    scriptPath,
                    request,
                    RuntimePaths.FindFfmpegExecutable(_projectRoot),
                    RuntimePaths.FindBrowserExecutable(),
                    supportedPresets)),
                EnableRaisingEvents = true,
            };
            _process = process;
        }

        process.OutputDataReceived += (_, e) => OnOutputLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnErrorLine(e.Data);
        process.Exited += (_, _) => OnProcessExited(process, generation);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动渲染进程。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process)) _process = null;
            }
            process.Dispose();
            throw;
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(string executable, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    private async void OnProcessExited(Process process, int generation)
    {
        bool success;
        try
        {
            await process.WaitForExitAsync();
            process.WaitForExit();
            success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RenderRunner] 等待进程退出失败：{ex.Message}");
            success = false;
        }

        lock (_sync)
        {
            if (generation != _generation || !ReferenceEquals(_process, process)) return;
            _process = null;
        }

        Completed?.Invoke(this, success);
        process.Dispose();
    }

    private void OnOutputLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("@@PROGRESS ", StringComparison.Ordinal))
        {
            EmitProgress(line["@@PROGRESS ".Length..]);
            return;
        }
        if (line.StartsWith("@@ERROR ", StringComparison.Ordinal))
        {
            RaiseError(line["@@ERROR ".Length..]);
            return;
        }
        LogReceived?.Invoke(this, line);
    }

    private void OnErrorLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) LogReceived?.Invoke(this, line);
    }

    private void EmitProgress(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Progress?.Invoke(this, new RenderProgressEventArgs
            {
                Stage = GetString(root, "stage") ?? "",
                Percent = GetInt32(root, "percent"),
                Frames = GetInt32(root, "frames"),
                LyricLines = GetInt32(root, "lyricLines"),
                ElapsedMs = GetInt64(root, "elapsedMs"),
                // 新协议使用 durationMs；兼容旧脚本 totalMs。
                TotalMs = GetInt64(root, "durationMs", "totalMs"),
                OutputPath = GetString(root, "outPath"),
                Bytes = GetInt64(root, "bytes"),
            });
        }
        catch (JsonException ex)
        {
            LogReceived?.Invoke(this, $"无法解析渲染进度：{ex.Message}");
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static int GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    private static long GetInt64(JsonElement root, string name, string? fallback = null)
    {
        if (root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result)) return result;
        return fallback is not null && root.TryGetProperty(fallback, out value) && value.TryGetInt64(out result)
            ? result : 0;
    }

    private void RaiseError(string json)
    {
        string message = json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            message = GetString(doc.RootElement, "message") ?? json;
        }
        catch (JsonException)
        {
            // 原样展示非 JSON 错误。
        }
        Progress?.Invoke(this, new RenderProgressEventArgs { Stage = "error", Error = message });
    }

    public void Cancel()
    {
        Process? process;
        lock (_sync) process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[RenderRunner] 取消失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        lock (_sync)
        {
            _generation++;
            _process?.Dispose();
            _process = null;
        }
        GC.SuppressFinalize(this);
    }
}
