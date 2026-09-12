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

    private const string NavigationGenerationKey = "__amllPreviewGeneration";
    private const int StartupDiagnosticAttempts = 30;
    private static readonly TimeSpan ReadinessRecoveryInterval = TimeSpan.FromSeconds(2);

    private AppState? _state;
    private LocalAssetServer? _server;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _patchTimer;
    private readonly DispatcherTimer _assetReloadTimer;
    private bool _webViewReady;
    private bool _pageReady;
    private bool _isLoaded;
    private bool _isActive = true;
    private bool _assetReloadPending = true;
    private bool _visualPatchPending;
    private bool _polling;
    private bool _patching;
    private bool _updatingSeek;
    private bool _initialized;
    private long _visualPatchRevision;
    private long _navigationGeneration;
    private long _activeNavigationGeneration;
    private ulong _activeNavigationId;
    private long _activeNavigationVisualRevision = -1;
    private string? _reportedReadinessError;
    private DateTimeOffset _nextReadinessRecoveryAt = DateTimeOffset.MaxValue;

    public PreviewPage()
    {
        InitializeComponent();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += OnPollTick;
        _patchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _patchTimer.Tick += OnPatchTick;
        _assetReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _assetReloadTimer.Tick += OnAssetReloadTick;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SetActive(Visibility == Visibility.Visible));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public nint WindowHandle { get; set; }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active)
        {
            StopRefreshTimers();
            return;
        }
        ProcessPendingRefresh();
        UpdatePollTimer();
    }

    private bool CanProcessRefresh => _isLoaded && _isActive;
    private bool CanUseReadyPage => CanProcessRefresh && _webViewReady && _pageReady;

    private void StopRefreshTimers()
    {
        _pollTimer.Stop();
        _patchTimer.Stop();
        _assetReloadTimer.Stop();
    }

    private void UpdatePollTimer()
    {
        if (CanProcessRefresh && _webViewReady) _pollTimer.Start();
        else _pollTimer.Stop();
    }

    public void RequestAssetRefresh()
    {
        _assetReloadPending = true;
        if (_isLoaded && _isActive)
        {
            _assetReloadTimer.Stop();
            _assetReloadTimer.Start();
        }
    }

    public void Initialize(AppState state, LocalAssetServer server)
    {
        if (_initialized) return;
        _initialized = true;
        _state = state;
        _server = server;
        _state.PropertyChanged += OnStatePropertyChanged;
        _ = EnsureWebViewAsync();
    }

    /// <summary>语言切换回调：刷新页面静态文案与素材摘要。</summary>
    public void ApplyLocalization()
    {
        PageTitleText.Text = LocalizationService.T("Preview.Title");
        PageSubtitleText.Text = LocalizationService.T("Preview.Subtitle");
        ReloadButtonText.Text = LocalizationService.T("Preview.ReloadButton");
        ToolTipService.SetToolTip(ReloadButton, LocalizationService.T("Preview.ReloadTooltip"));
        AudioButtonText.Text = LocalizationService.T("Preview.Audio");
        ToolTipService.SetToolTip(PickAudioButton, LocalizationService.T("Preview.AudioTooltip"));
        LyricButtonText.Text = LocalizationService.T("Preview.Lyric");
        ToolTipService.SetToolTip(PickLyricButton, LocalizationService.T("Preview.LyricTooltip"));
        CoverButtonText.Text = LocalizationService.T("Preview.Cover");
        ToolTipService.SetToolTip(PickCoverButton, LocalizationService.T("Preview.CoverTooltip"));
        CurrentAssetsText.Text = LocalizationService.T("Preview.CurrentAssets");
        EmptyTitleText.Text = LocalizationService.T("Preview.EmptyTitle");
        EmptyHintText.Text = LocalizationService.T("Preview.EmptyHint");
        ToolTipService.SetToolTip(ToStartButton, LocalizationService.T("Preview.ToStartTooltip"));
        ToolTipService.SetToolTip(BackButton, LocalizationService.T("Preview.Back5Tooltip"));
        ToolTipService.SetToolTip(PlayButton, LocalizationService.T("Preview.PlayTooltip"));
        ToolTipService.SetToolTip(ForwardButton, LocalizationService.T("Preview.Forward5Tooltip"));
        ToolTipService.SetToolTip(SeekSlider, LocalizationService.T("Preview.SeekTooltip"));
        if (_state is not null) AssetSummaryText.Text = BuildSummary();
        else AssetSummaryText.Text = LocalizationService.T("Preview.NoAssets");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ProcessPendingRefresh();
        UpdatePollTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopRefreshTimers();
    }

    private void ProcessPendingRefresh()
    {
        if (!CanProcessRefresh) return;
        if (_assetReloadPending)
        {
            if (!_webViewReady) return;
            _assetReloadTimer.Stop();
            _assetReloadTimer.Start();
            return;
        }
        ScheduleVisualPatch();
    }

    private void ScheduleVisualPatch()
    {
        if (!_visualPatchPending || !CanUseReadyPage) return;
        _patchTimer.Stop();
        _patchTimer.Start();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ProjectRoot) or nameof(AppState.HasAudio)) return;
        if (e.PropertyName is nameof(AppState.Preset) or nameof(AppState.CustomStageWidth) or nameof(AppState.CustomStageHeight))
        {
            // 舞台尺寸摘要立即刷新，不等下一次素材重载。
            if (_state is not null) AssetSummaryText.Text = BuildSummary();
        }
        if (IsAssetProperty(e.PropertyName))
        {
            RequestAssetRefresh();
            return;
        }
        if (!IsVisualProperty(e.PropertyName)) return;
        _visualPatchRevision++;
        _visualPatchPending = true;
        ScheduleVisualPatch();
    }

    private void OnAssetReloadTick(object? sender, object e)
    {
        _assetReloadTimer.Stop();
        if (!CanProcessRefresh || !_webViewReady) return;
        ReloadPreview();
    }

    private static bool IsAssetProperty(string? propertyName) => propertyName is
        nameof(AppState.AudioPath) or nameof(AppState.LyricPath) or nameof(AppState.CoverPath);

    internal static bool IsVisualProperty(string? propertyName) => propertyName is
        nameof(AppState.Title) or nameof(AppState.Artist) or nameof(AppState.Album) or
        nameof(AppState.FontFamily) or nameof(AppState.FontScale) or nameof(AppState.FontWeight) or
        nameof(AppState.SecondaryMode) or nameof(AppState.ShowSongInfo) or nameof(AppState.ShowArtist) or
        nameof(AppState.ShowAlbum) or nameof(AppState.ShowArtwork) or
        nameof(AppState.ShowControls) or nameof(AppState.ArtworkScale) or nameof(AppState.LyricAlign) or
        nameof(AppState.LyricOffset) or nameof(AppState.LyricWidth) or nameof(AppState.LyricHeight) or
        nameof(AppState.InfoOffsetX) or nameof(AppState.InfoOffsetY) or nameof(AppState.LyricFade) or
        nameof(AppState.BackgroundDim) or
        nameof(AppState.BackgroundSaturation) or nameof(AppState.BackgroundBlur) or nameof(AppState.FlowSpeed) or
        nameof(AppState.RenderScale) or nameof(AppState.Preset) or nameof(AppState.Fps) or
        nameof(AppState.CustomStageWidth) or nameof(AppState.CustomStageHeight);

    private async void OnPatchTick(object? sender, object e)
    {
        _patchTimer.Stop();
        if (_patching || !CanUseReadyPage || _state is null)
        {
            _visualPatchPending = true;
            return;
        }

        long patchRevision = _visualPatchRevision;
        long navigationGeneration = _activeNavigationGeneration;
        ulong navigationId = _activeNavigationId;
        _patching = true;
        try
        {
            string? result = await EvalStringAsync(
                PreviewScripts.ApplyOptions(_state), navigationGeneration, navigationId);
            if (!IsCurrentNavigation(navigationGeneration, navigationId))
            {
                _visualPatchPending = _visualPatchRevision > _activeNavigationVisualRevision;
                return;
            }

            bool applied = result is not null && result.Contains("\"ok\":true", StringComparison.Ordinal);
            if (!applied)
            {
                EnterReadinessRecovery(
                    navigationGeneration,
                    navigationId,
                    LocalizationService.T("Preview.BridgeNotReady"));
                return;
            }

            if (patchRevision != _visualPatchRevision)
            {
                _visualPatchPending = true;
                return;
            }
            _visualPatchPending = false;
        }
        finally
        {
            _patching = false;
            ScheduleVisualPatch();
        }
    }

    private void EnterReadinessRecovery(long navigationGeneration, ulong navigationId, string message)
    {
        if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
        _pageReady = false;
        _visualPatchPending = true;
        _patchTimer.Stop();
        _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
        ShowDiagnostic(message);
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
                DispatcherQueue.TryEnqueue(() => ShowDiagnostic(LocalizationService.T("Preview.PageError", args.TryGetWebMessageAsString())));
            await Stage.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('error',e=>chrome.webview.postMessage(e.message||'页面脚本异常'));" +
                "window.addEventListener('unhandledrejection',e=>chrome.webview.postMessage(String(e.reason||'未处理的脚本异常')));");
#if DEBUG
            Stage.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            Stage.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            Stage.CoreWebView2.ProcessFailed += (_, args) =>
                DispatcherQueue.TryEnqueue(() => ShowError(LocalizationService.T("Preview.ProcessFailed", args.ProcessFailedKind)));
            _webViewReady = true;
            LoadingRing.IsActive = false;
            UpdatePollTimer();
            ProcessPendingRefresh();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            ShowError(LocalizationService.T("Preview.WebViewFailed", ex.Message));
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
        if (!CanProcessRefresh || !_webViewReady || _state is null || _server is null) return;
        _assetReloadPending = false;
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
            ShowError(LocalizationService.T("Preview.NoDist"));
            return;
        }

        var query = BuildPreviewQuery(_state, _server).ToList();
        long navigationGeneration = ++_navigationGeneration;
        query.Add($"{NavigationGenerationKey}={navigationGeneration}");
        _activeNavigationGeneration = navigationGeneration;
        _activeNavigationId = 0;
        _activeNavigationVisualRevision = _visualPatchRevision;
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
        string lyric = string.IsNullOrWhiteSpace(_state?.LyricPath) ? LocalizationService.T("Preview.NoLyric") : Path.GetFileName(_state.LyricPath);
        string cover = string.IsNullOrWhiteSpace(_state?.CoverPath) ? LocalizationService.T("Preview.NoCover") : Path.GetFileName(_state.CoverPath);
        (int width, int height) = StagePresets.EffectiveDimensions(
            _state?.Preset ?? "1080p", _state?.CustomStageWidth ?? 1920, _state?.CustomStageHeight ?? 1080);
        FpsText.Text = $"{width} × {height} px · {_state?.Fps ?? 0} FPS";
        return string.IsNullOrWhiteSpace(audio)
            ? LocalizationService.T("Preview.NoAssets")
            : LocalizationService.T("Preview.Summary", audio, lyric, cover);
    }

    private async Task<string?> EvalStringAsync(
        string script, long navigationGeneration, ulong navigationId)
    {
        if (!_webViewReady || Stage.CoreWebView2 is null ||
            !IsCurrentNavigation(navigationGeneration, navigationId)) return null;
        try
        {
            string raw = await Stage.ExecuteScriptAsync(script);
            if (!IsCurrentNavigation(navigationGeneration, navigationId)) return null;
            return PreviewScripts.DecodeExecuteScriptResult(raw);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[PreviewPage] JS 执行失败：{ex.Message}");
            return null;
        }
    }

    private async void OnPollTick(object? sender, object e)
    {
        if (_polling || !CanProcessRefresh) return;
        _polling = true;
        try
        {
            if (CanProcessRefresh) await PollAsync();
        }
        finally { _polling = false; }
    }

    private async Task PollAsync()
    {
        long navigationGeneration = _activeNavigationGeneration;
        ulong navigationId = _activeNavigationId;
        if (!_pageReady)
        {
            await TryRecoverPageReadinessAsync(navigationGeneration, navigationId);
            if (!IsCurrentNavigation(navigationGeneration, navigationId) || !_pageReady) return;
        }

        string? json = await EvalStringAsync(
            "(function(){const a=document.querySelector('audio');if(!a)return JSON.stringify({ready:false});" +
            "return JSON.stringify({ready:true,t:a.currentTime,d:Number.isFinite(a.duration)?a.duration:0,p:a.paused});})()",
            navigationGeneration, navigationId);
        if (!IsCurrentNavigation(navigationGeneration, navigationId) || json is null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!IsCurrentNavigation(navigationGeneration, navigationId) ||
                !CanProcessRefresh || !root.TryGetProperty("ready", out JsonElement ready) || !ready.GetBoolean()) return;
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

        if (!TryGetNavigationGeneration(args.Uri, out long navigationGeneration))
        {
            args.Cancel = true;
            ShowDiagnostic(LocalizationService.T("Preview.BlockedNav"));
            return;
        }

        if (navigationGeneration < _activeNavigationGeneration)
        {
            args.Cancel = true;
            return;
        }
        _activeNavigationGeneration = navigationGeneration;
        _activeNavigationId = args.NavigationId;
        _pageReady = false;
        _reportedReadinessError = null;
        _nextReadinessRecoveryAt = DateTimeOffset.MaxValue;
        BuildHintText.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
    }

    private bool TryGetNavigationGeneration(string? uri, out long generation)
    {
        generation = 0;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? target)) return false;
        foreach (string item in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            if (pair.Length == 2 && pair[0] == NavigationGenerationKey &&
                long.TryParse(Uri.UnescapeDataString(pair[1]), out generation)) return true;
        }
        return false;
    }

    private bool IsCurrentNavigation(long navigationGeneration, ulong navigationId) =>
        navigationGeneration == _activeNavigationGeneration && navigationId == _activeNavigationId;

    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        long navigationGeneration = _activeNavigationGeneration;
        ulong navigationId = args.NavigationId;
        if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;

        LoadingRing.IsActive = false;
        if (!args.IsSuccess)
        {
            ShowError(LocalizationService.T("Preview.NavFailed", args.WebErrorStatus));
            return;
        }

        JsonElement lastDiagnostic = default;
        bool hasDiagnostic = false;
        for (int attempt = 0; attempt < StartupDiagnosticAttempts; attempt++)
        {
            if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
            string? diagnostic = await EvalStringAsync(
                PreviewScripts.Diagnose(), navigationGeneration, navigationId);
            if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
            if (diagnostic is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(diagnostic);
                    lastDiagnostic = document.RootElement.Clone();
                    hasDiagnostic = true;
                    string? error = PreviewReadiness.GetError(lastDiagnostic);
                    if (error is not null)
                    {
                        _reportedReadinessError = error;
                        ShowDiagnostic(LocalizationService.T("Preview.AppFailed", error));
                        _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
                        return;
                    }
                    if (PreviewReadiness.IsReady(lastDiagnostic))
                    {
                        MarkPageReady(navigationGeneration, navigationId);
                        return;
                    }
                }
                catch (JsonException ex)
                {
                    ShowDiagnostic(LocalizationService.T("Preview.DiagnosticInvalid", ex.Message));
                    _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
                    return;
                }
            }
            if (attempt + 1 < StartupDiagnosticAttempts)
            {
                await Task.Delay(PreviewReadiness.GetRetryDelay(attempt));
                if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
            }
        }

        if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
        if (!hasDiagnostic)
        {
            ShowDiagnostic(LocalizationService.T("Preview.NoDiagnostic"));
            _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
            return;
        }

        bool appStarted = HasPreviewStarted(lastDiagnostic);
        ShowDiagnostic(appStarted
            ? LocalizationService.T("Preview.StageNotReady", PreviewReadiness.Describe(lastDiagnostic))
            : LocalizationService.T("Preview.BridgeNotStarted"));
        if (IsCurrentNavigation(navigationGeneration, navigationId))
            _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
    }

    private static bool HasPreviewStarted(JsonElement diagnostic) =>
        HasTrueProperty(diagnostic, "rendered") &&
        HasTrueProperty(diagnostic, "bridge") &&
        HasTrueProperty(diagnostic, "setOptions");

    private static bool HasTrueProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private async Task TryRecoverPageReadinessAsync(long navigationGeneration, ulong navigationId)
    {
        if (!IsCurrentNavigation(navigationGeneration, navigationId) ||
            DateTimeOffset.UtcNow < _nextReadinessRecoveryAt) return;
        _nextReadinessRecoveryAt = DateTimeOffset.UtcNow + ReadinessRecoveryInterval;
        string? diagnostic = await EvalStringAsync(
            PreviewScripts.Diagnose(), navigationGeneration, navigationId);
        if (!IsCurrentNavigation(navigationGeneration, navigationId) || diagnostic is null) return;

        try
        {
            using var document = JsonDocument.Parse(diagnostic);
            JsonElement readiness = document.RootElement;
            string? error = PreviewReadiness.GetError(readiness);
            if (error is not null)
            {
                if (!string.Equals(error, _reportedReadinessError, StringComparison.Ordinal))
                {
                    _reportedReadinessError = error;
                    ShowDiagnostic(LocalizationService.T("Preview.AppFailed", error));
                }
                return;
            }

            if (PreviewReadiness.IsReady(readiness))
                MarkPageReady(navigationGeneration, navigationId);
        }
        catch (JsonException)
        {
            // The periodic poll retries transient or partially written diagnostic payloads.
        }
    }

    private void MarkPageReady(long navigationGeneration, ulong navigationId)
    {
        if (!IsCurrentNavigation(navigationGeneration, navigationId)) return;
        _pageReady = true;
        LoadingRing.IsActive = false;
        BuildHintText.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        if (_activeNavigationVisualRevision == _visualPatchRevision)
            _visualPatchPending = false;
        ScheduleVisualPatch();
    }

    private void RequestManualReload()
    {
        _assetReloadPending = true;
        if (!CanProcessRefresh || !_webViewReady) return;
        _assetReloadTimer.Stop();
        ReloadPreview();
    }

    private async Task ExecuteForCurrentNavigationAsync(string script)
    {
        long navigationGeneration = _activeNavigationGeneration;
        ulong navigationId = _activeNavigationId;
        await EvalStringAsync(script, navigationGeneration, navigationId);
    }

    private async void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        await ExecuteForCurrentNavigationAsync(PreviewScripts.TogglePlay());
        await PollAsync();
    }

    private async void OnToStart(object sender, RoutedEventArgs e)
    {
        await ExecuteForCurrentNavigationAsync(PreviewScripts.SeekTo(0));
        await PollAsync();
    }

    private async void OnBack5(object sender, RoutedEventArgs e) => await SeekByAsync(-5);
    private async void OnForward5(object sender, RoutedEventArgs e) => await SeekByAsync(5);

    private async Task SeekByAsync(double deltaSeconds)
    {
        await ExecuteForCurrentNavigationAsync(PreviewScripts.SeekBy(deltaSeconds));
        await PollAsync();
    }

    private void OnReload(object sender, RoutedEventArgs e) => RequestManualReload();

    private async void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || !_webViewReady) return;
        await ExecuteForCurrentNavigationAsync(PreviewScripts.SeekTo(e.NewValue));
    }

    private async Task ApplyPickedAssetAsync(Func<string?> getCurrentPath, Action<string> setPath, string title,
        IReadOnlyCollection<string> extensions, PickerLocationId location)
    {
        string? path = await PickFileAsync(title, extensions, location);
        if (path is null || _state is null) return;
        if (string.Equals(getCurrentPath(), path, StringComparison.OrdinalIgnoreCase)) _state.NotifyAssetReselected(path);
        else setPath(path);
    }

    private async void OnPickAudio(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.AudioPath, value => _state!.AudioPath = value,
            LocalizationService.T("Preview.PickAudio"), AudioExtensions, PickerLocationId.MusicLibrary);

    private async void OnPickLyric(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.LyricPath, value => _state!.LyricPath = value,
            LocalizationService.T("Preview.PickLyric"), LyricExtensions, PickerLocationId.DocumentsLibrary);

    private async void OnPickCover(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.CoverPath, value => _state!.CoverPath = value,
            LocalizationService.T("Preview.PickCover"), CoverExtensions, PickerLocationId.PicturesLibrary);

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
