using System.ComponentModel;
using System.Text.Json;
using AmllMvStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;

namespace AmllMvStudio.Views;

public sealed partial class PreviewPage : UserControl
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus" };
    private static readonly HashSet<string> LyricExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttml", ".xml", ".lrc", ".alrc", ".yrc", ".qrc", ".eslrc", ".lys", ".lyl", ".lqe" };
    private static readonly HashSet<string> CoverExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    private AppState? _state;
    private LocalAssetServer? _server;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _patchTimer;
    private bool _webViewReady;
    private bool _pageReady;
    private bool _isLoaded;
    private bool _polling;
    private bool _patching;
    private bool _updatingSeek;
    private bool _initialized;

    public PreviewPage()
    {
        InitializeComponent();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += OnPollTick;
        _patchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _patchTimer.Tick += OnPatchTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public nint WindowHandle { get; set; }

    public void Initialize(AppState state, LocalAssetServer server)
    {
        if (_initialized) return;
        _initialized = true;
        _state = state;
        _server = server;
        _state.PropertyChanged += OnStatePropertyChanged;
        _ = EnsureWebViewAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_webViewReady) _pollTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _pollTimer.Stop();
        _patchTimer.Stop();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ProjectRoot) or nameof(AppState.HasAudio)) return;
        if (!_isLoaded) return;
        if (IsAssetProperty(e.PropertyName))
        {
            ReloadPreview();
            return;
        }
        if (!IsVisualProperty(e.PropertyName)) return;
        _patchTimer.Stop();
        _patchTimer.Start();
    }

    private static bool IsAssetProperty(string? propertyName) => propertyName is
        nameof(AppState.AudioPath) or nameof(AppState.LyricPath) or nameof(AppState.CoverPath);

    internal static bool IsVisualProperty(string? propertyName) => propertyName is
        nameof(AppState.Title) or nameof(AppState.Artist) or nameof(AppState.Album) or
        nameof(AppState.FontFamily) or nameof(AppState.FontScale) or nameof(AppState.FontWeight) or
        nameof(AppState.SecondaryMode) or nameof(AppState.ShowSongInfo) or nameof(AppState.ShowArtist) or
        nameof(AppState.ShowAlbum) or nameof(AppState.ShowArtwork) or
        nameof(AppState.ShowControls) or nameof(AppState.ArtworkScale) or nameof(AppState.LyricAlign) or
        nameof(AppState.LyricOffset) or nameof(AppState.LyricWidth) or nameof(AppState.LyricFade) or
        nameof(AppState.BackgroundDim) or
        nameof(AppState.BackgroundSaturation) or nameof(AppState.BackgroundBlur) or nameof(AppState.FlowSpeed) or
        nameof(AppState.RenderScale) or nameof(AppState.Preset) or nameof(AppState.Fps);

    private async void OnPatchTick(object? sender, object e)
    {
        _patchTimer.Stop();
        if (_patching || !_pageReady || _state is null) return;
        _patching = true;
        try
        {
            string? result = await EvalStringAsync(PreviewScripts.ApplyOptions(_state));
            if (result is null || !result.Contains("\"ok\":true", StringComparison.Ordinal))
                ShowDiagnostic("预览桥接尚未启动，实时设置未能应用。请手动刷新预览。");
        }
        finally { _patching = false; }
    }

    private async Task EnsureWebViewAsync()
    {
        try
        {
            LoadingRing.IsActive = true;
            string userDataDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AmllMvStudio", "WebView2");
            Directory.CreateDirectory(userDataDir);
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --ignore-gpu-blocklist",
            };
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, options);
            await Stage.EnsureCoreWebView2Async(environment);
            Stage.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Stage.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Stage.CoreWebView2.WebMessageReceived += (_, args) =>
                DispatcherQueue.TryEnqueue(() => ShowDiagnostic($"预览页面错误：{args.TryGetWebMessageAsString()}"));
            await Stage.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('error',e=>chrome.webview.postMessage(e.message||'页面脚本异常'));" +
                "window.addEventListener('unhandledrejection',e=>chrome.webview.postMessage(String(e.reason||'未处理的脚本异常')));");
#if DEBUG
            Stage.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            Stage.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            Stage.CoreWebView2.ProcessFailed += (_, args) =>
                DispatcherQueue.TryEnqueue(() => ShowError($"预览进程异常：{args.ProcessFailedKind}"));
            _webViewReady = true;
            LoadingRing.IsActive = false;
            if (_isLoaded) _pollTimer.Start();
            ReloadPreview();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            ShowError($"WebView2 初始化失败：{ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        BuildHintText.Text = message;
        BuildHintText.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Visible;
        Stage.Visibility = Visibility.Collapsed;
    }

    private void ShowDiagnostic(string message)
    {
        BuildHintText.Text = message;
        BuildHintText.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Visible;
        Stage.Visibility = Visibility.Visible;
        LoadingRing.IsActive = false;
    }

    private void ReloadPreview()
    {
        if (!_webViewReady || _state is null || _server is null) return;
        _patchTimer.Stop();
        _pageReady = false;
        AssetSummaryText.Text = BuildSummary();
        if (!_state.HasAudio)
        {
            Stage.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            BuildHintText.Visibility = Visibility.Collapsed;
            TimeText.Text = "0:00 / 0:00";
            return;
        }

        string distIndex = Path.Combine(_state.ProjectRoot, "dist", "index.html");
        string rootIndex = Path.Combine(_state.ProjectRoot, "index.html");
        if (!File.Exists(distIndex) && !File.Exists(rootIndex))
        {
            ShowError("找不到预览页面 dist/index.html。请确认发布内容完整，或在项目目录构建前端。");
            return;
        }

        var query = BuildPreviewQuery(_state, _server);
        Stage.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        Stage.CoreWebView2.Navigate($"{_server.BaseUrl}/?{string.Join("&", query)}");
    }

    internal static IReadOnlyList<string> BuildPreviewQuery(AppState state, LocalAssetServer server) =>
        PreviewScripts.BuildQuery(state, server);

    private string BuildSummary()
    {
        string audio = Path.GetFileName(_state?.AudioPath ?? "");
        string lyric = string.IsNullOrWhiteSpace(_state?.LyricPath) ? "无歌词" : Path.GetFileName(_state.LyricPath);
        string cover = string.IsNullOrWhiteSpace(_state?.CoverPath) ? "无封面" : Path.GetFileName(_state.CoverPath);
        StagePreset preset = StagePresets.Resolve(_state?.Preset);
        FpsText.Text = $"{preset.Width} × {preset.Height} px · {_state?.Fps ?? 0} FPS";
        return string.IsNullOrWhiteSpace(audio) ? "尚未载入素材" : $"{audio} · {lyric} · {cover}";
    }

    private async Task<string?> EvalStringAsync(string script)
    {
        if (!_webViewReady || Stage.CoreWebView2 is null) return null;
        try
        {
            string raw = await Stage.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString() : doc.RootElement.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[PreviewPage] JS 执行失败：{ex.Message}");
            return null;
        }
    }

    private async void OnPollTick(object? sender, object e)
    {
        if (_polling) return;
        _polling = true;
        try { await PollAsync(); }
        finally { _polling = false; }
    }

    private async Task PollAsync()
    {
        string? json = await EvalStringAsync(
            "(function(){const a=document.querySelector('audio');if(!a)return JSON.stringify({ready:false});" +
            "return JSON.stringify({ready:true,t:a.currentTime,d:Number.isFinite(a.duration)?a.duration:0,p:a.paused});})()");
        if (json is null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("ready", out JsonElement ready) || !ready.GetBoolean()) return;
            // TryGetProperty + 默认值：字段缺失不应经 async void 落到全局 UnhandledException
            double current = root.TryGetProperty("t", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
            double duration = root.TryGetProperty("d", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            bool paused = !(root.TryGetProperty("p", out JsonElement p) && p.ValueKind == JsonValueKind.False);
            LoadingRing.IsActive = false;
            TimeText.Text = $"{FormatClock(current)} / {FormatClock(duration)}";
            PlayIcon.Symbol = paused ? Symbol.Play : Symbol.Pause;
            _updatingSeek = true;
            SeekSlider.Minimum = 0;
            SeekSlider.Maximum = Math.Max(1, duration);
            SeekSlider.Value = Math.Clamp(current, 0, Math.Max(1, duration));
            _updatingSeek = false;
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
    }

    private static string FormatClock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:00}";
    }

    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // 导航白名单：内嵌预览只允许本地资产服务源，防止页面内跳转把 WebView2 带到外部站点
        if (_server is not null && args.Uri is { } target &&
            !target.StartsWith($"http://127.0.0.1:{_server.Port}/", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            return;
        }
        _pageReady = false;
        BuildHintText.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
    }

    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        if (!args.IsSuccess)
        {
            ShowError($"预览导航失败：{args.WebErrorStatus}");
            return;
        }

        string? diagnostic = await EvalStringAsync(PreviewScripts.Diagnose());
        if (diagnostic is null)
        {
            ShowDiagnostic("预览页面已加载，但无法执行诊断脚本。请检查 WebView2 运行时。");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(diagnostic);
            JsonElement root = document.RootElement;
            bool rendered = root.TryGetProperty("rendered", out JsonElement renderedValue) && renderedValue.GetBoolean();
            bool bridge = root.TryGetProperty("bridge", out JsonElement bridgeValue) && bridgeValue.GetBoolean();
            bool setOptions = root.TryGetProperty("setOptions", out JsonElement setterValue) && setterValue.GetBoolean();
            string? error = root.TryGetProperty("error", out JsonElement errorValue) && errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString() : null;
            if (!rendered || !bridge || !setOptions)
            {
                ShowDiagnostic(error is not null
                    ? $"预览应用启动失败：{error}"
                    : "预览页面已载入，但 React 或实时桥接未启动。请确认 dist 构建产物完整后刷新。");
                return;
            }
            _pageReady = true;
            BuildHintText.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        catch (JsonException ex)
        {
            ShowDiagnostic($"预览启动诊断返回无效数据：{ex.Message}");
        }
    }

    private async void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        await EvalStringAsync(PreviewScripts.TogglePlay());
        await PollAsync();
    }

    private async void OnToStart(object sender, RoutedEventArgs e)
    {
        await EvalStringAsync(PreviewScripts.SeekTo(0));
        await PollAsync();
    }

    private async void OnBack5(object sender, RoutedEventArgs e) => await SeekByAsync(-5);
    private async void OnForward5(object sender, RoutedEventArgs e) => await SeekByAsync(5);

    private async Task SeekByAsync(double deltaSeconds)
    {
        await EvalStringAsync(PreviewScripts.SeekBy(deltaSeconds));
        await PollAsync();
    }

    private void OnReload(object sender, RoutedEventArgs e) => ReloadPreview();

    private async void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || !_webViewReady) return;
        await EvalStringAsync(PreviewScripts.SeekTo(e.NewValue));
    }

    private async void OnPickAudio(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync("选择音频文件", AudioExtensions, PickerLocationId.MusicLibrary);
        if (path is not null && _state is not null) _state.AudioPath = path;
    }

    private async void OnPickLyric(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync("选择歌词文件", LyricExtensions, PickerLocationId.DocumentsLibrary);
        if (path is not null && _state is not null) _state.LyricPath = path;
    }

    private async void OnPickCover(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync("选择专辑封面", CoverExtensions, PickerLocationId.PicturesLibrary);
        if (path is not null && _state is not null) _state.CoverPath = path;
    }

    private async Task<string?> PickFileAsync(
        string title, IReadOnlyCollection<string> extensions, PickerLocationId location)
    {
        if (WindowHandle == nint.Zero) return null;
        var picker = new FileOpenPicker { SuggestedStartLocation = location, CommitButtonText = title };
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        return extensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) && File.Exists(file.Path)
            ? file.Path : null;
    }
}
