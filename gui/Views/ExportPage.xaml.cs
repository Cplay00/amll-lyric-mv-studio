using System.ComponentModel;
using System.Text;
using AmllMvStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;

namespace AmllMvStudio.Views;

public sealed partial class ExportPage : UserControl
{
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus"];
    private static readonly string[] LyricExtensions = [".ttml", ".xml", ".lrc", ".alrc", ".yrc", ".qrc", ".eslrc", ".lys", ".lyl", ".lqe"];
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private AppState? _state;
    private RenderRunner? _runner;
    private string? _lastOutputPath;
    private string? _overwriteConfirmedPath;
    private readonly StringBuilder _log = new();
    private bool _syncing = true;
    private bool _ready;

    public ExportPage()
    {
        InitializeComponent();
        PresetCombo.ItemsSource = StagePresets.All;
        FpsSlider.Minimum = 24;
        FpsSlider.Maximum = 60;
        FpsSlider.StepFrequency = 1;
        ArtworkScaleSlider.Minimum = 0.5;
        ArtworkScaleSlider.Maximum = 1.5;
        RenderScaleSlider.Minimum = 0.25;
        RenderScaleSlider.Maximum = 1;
        FontScaleSlider.Minimum = 0.5;
        FontScaleSlider.Maximum = 2;
        LyricWidthSlider.Minimum = RenderContracts.MinLyricWidth;
        LyricWidthSlider.Maximum = RenderContracts.MaxLyricWidth;
        LyricFadeSlider.Minimum = RenderContracts.MinLyricFade;
        LyricFadeSlider.Maximum = RenderContracts.MaxLyricFade;
        QualitySlider.Minimum = 10;
        QualitySlider.Maximum = 30;
        PresetCombo.DisplayMemberPath = nameof(StagePreset.DisplayLabel);
        FontFamilyCombo.ItemsSource = SystemFontCatalog.GetFontFamilies();
        FontWeightCombo.ItemsSource = Enumerable.Range(1, 9).Select(value => value * 100).ToArray();
        SecondaryModeCombo.ItemsSource = RenderContracts.SecondaryModes;
        LyricAlignCombo.ItemsSource = new[] { "left", "center", "right" };
        BrowserGpuCombo.ItemsSource = RenderContracts.BrowserGpuModes;
        AudioCodecCombo.ItemsSource = RenderContracts.AudioCodecs;
        AudioBitrateCombo.ItemsSource = new[] { "128k", "192k", "256k", "320k" };
        Unloaded += (_, _) => _runner?.Cancel();
    }

    /// <summary>应用退出时同步取消渲染进程树（Kill 是同步语义），防止遗留孤儿 node/Chromium/ffmpeg。</summary>
    public void Shutdown() => _runner?.Dispose();

    public nint WindowHandle { get; set; }

    public void Initialize(AppState state)
    {
        if (_ready) return;
        _state = state;
        _runner = new RenderRunner(state.ProjectRoot);
        _runner.Progress += OnRenderProgress;
        _runner.LogReceived += OnRenderLog;
        _runner.Completed += OnRenderCompleted;
        _state.PropertyChanged += OnStatePropertyChanged;
        SyncAllFromState();
        _ready = true;
        _syncing = false;
        UpdateLabels();
        _ = LoadEncodersAsync();
    }

    private void SyncAllFromState()
    {
        if (_state is null) return;
        _syncing = true;
        TitleBox.Text = _state.Title; ArtistBox.Text = _state.Artist; AlbumBox.Text = _state.Album;
        FontFamilyCombo.Text = _state.FontFamily;
        FpsSlider.Value = _state.Fps; ArtworkScaleSlider.Value = _state.ArtworkScale;
        FlowSlider.Value = _state.FlowSpeed; RenderScaleSlider.Value = _state.RenderScale;
        FontScaleSlider.Value = _state.FontScale; LyricOffsetSlider.Value = _state.LyricOffset;
        LyricWidthSlider.Value = _state.LyricWidth; BackgroundDimSlider.Value = _state.BackgroundDim;
        BackgroundSaturationSlider.Value = _state.BackgroundSaturation; BackgroundBlurSlider.Value = _state.BackgroundBlur;
        QualitySlider.Value = _state.Quality; TailBox.Value = _state.Tail;
        ShowSongInfoToggle.IsOn = _state.ShowSongInfo; ShowArtworkToggle.IsOn = _state.ShowArtwork;
        ShowArtistToggle.IsOn = _state.ShowArtist; ShowAlbumToggle.IsOn = _state.ShowAlbum;
        ShowControlsToggle.IsOn = _state.ShowControls;
        LyricFadeSlider.Value = _state.LyricFade;
        SelectItem(PresetCombo, StagePresets.Resolve(_state.Preset));
        SelectValue(FontWeightCombo, _state.FontWeight); SelectValue(SecondaryModeCombo, _state.SecondaryMode);
        SelectValue(LyricAlignCombo, _state.LyricAlign); SelectValue(BrowserGpuCombo, _state.BrowserGpu);
        SelectValue(AudioCodecCombo, _state.AudioCodec); SelectValue(AudioBitrateCombo, _state.AudioBitrate);
        SyncAssetBadges();
    }

    private async Task LoadEncodersAsync()
    {
        if (_runner is null || _state is null) return;
        IReadOnlyList<EncoderOption> options = await _runner.ProbeEncodersAsync();
        EncoderCombo.ItemsSource = options;
        EncoderCombo.DisplayMemberPath = nameof(EncoderOption.DisplayLabel);
        EncoderOption? selected = options.FirstOrDefault(x => x.IsAvailable && x.Id == _state.Encoder)
            ?? options.FirstOrDefault(x => x.IsAvailable && x.Id == "auto")
            ?? options.FirstOrDefault(x => x.IsAvailable);
        EncoderCombo.SelectedItem = selected;
        if (selected is not null)
        {
            _state.Encoder = selected.Id;
            UpdateEncoderPresets(selected.Id, preserveCurrent: true, selected.UsesModernNvencPresets);
        }
        if (options.All(x => !x.IsAvailable || x.Id == "auto"))
            ShowInfo("未探测到明确可用的视频编码器；自动模式仍可尝试运行时回退。", InfoBarSeverity.Warning);
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || !_ready) return;
        if (e.PropertyName is nameof(AppState.AudioPath) or nameof(AppState.LyricPath) or nameof(AppState.CoverPath))
            SyncAssetBadges();
    }

    private void SyncAssetBadges()
    {
        if (_state is null) return;
        AudioBadge.Text = _state.HasAudio ? Path.GetFileName(_state.AudioPath) : "未选择";
        LyricBadge.Text = string.IsNullOrWhiteSpace(_state.LyricPath) ? "未选择" : Path.GetFileName(_state.LyricPath);
        CoverBadge.Text = string.IsNullOrWhiteSpace(_state.CoverPath) ? "未选择" : Path.GetFileName(_state.CoverPath);
    }

    private void OnConfigurationChanged(object sender, object e)
    {
        if (ReferenceEquals(sender, EncoderCombo) && EncoderCombo.SelectedItem is EncoderOption encoder)
            UpdateEncoderPresets(encoder.Id, preserveCurrent: false, encoder.UsesModernNvencPresets);
        UpdateLabels();
        PushToState();
    }

    private void OnFontFamilySubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        sender.Text = args.Text.Trim();
        PushToState();
        args.Handled = true;
    }

    private void OnFontFamilyLostFocus(object sender, RoutedEventArgs e) => PushToState();

    private void UpdateEncoderPresets(string encoder, bool preserveCurrent, bool modernNvenc)
    {
        string current = preserveCurrent ? _state?.EncoderPreset ?? "" : "";
        IReadOnlyList<string> presets = RenderContracts.GetEncoderPresets(encoder, modernNvenc);
        EncoderPresetCombo.ItemsSource = presets;
        EncoderPresetCombo.IsEnabled = presets.Count > 0;
        string selected = presets.Contains(current, StringComparer.Ordinal)
            ? current
            : RenderContracts.GetDefaultEncoderPreset(encoder, modernNvenc);
        EncoderPresetCombo.SelectedItem = selected.Length == 0 ? null : selected;
    }

    private void OnConfigurationRangeChanged(object sender, RangeBaseValueChangedEventArgs e) { UpdateLabels(); PushToState(); }
    private void OnNumberConfigurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => PushToState();

    private void UpdateLabels()
    {
        if (!_ready && !_syncing) return;
        FpsLabel.Text = $"帧率：{FpsSlider.Value:0} FPS";
        ArtworkScaleLabel.Text = $"封面缩放：{ArtworkScaleSlider.Value * 100:0}%";
        FlowLabel.Text = $"背景流速：{FlowSlider.Value:0.0}";
        RenderScaleLabel.Text = $"渲染倍率：{RenderScaleSlider.Value:0.00}×";
        FontScaleLabel.Text = $"字号缩放：{FontScaleSlider.Value * 100:0}%";
        LyricOffsetLabel.Text = $"垂直偏移：{LyricOffsetSlider.Value:0} px";
        LyricWidthLabel.Text = $"歌词区域宽度：{LyricWidthSlider.Value:0}%";
        LyricFadeLabel.Text = LyricFadeSlider.Value <= 0
            ? "上下边缘羽化：关闭"
            : $"上下边缘羽化：{LyricFadeSlider.Value:P0}";
        BackgroundDimLabel.Text = $"背景压暗：{BackgroundDimSlider.Value * 100:0}%";
        BackgroundSaturationLabel.Text = $"背景饱和度：{BackgroundSaturationSlider.Value * 100:0}%";
        BackgroundBlurLabel.Text = $"背景模糊：{BackgroundBlurSlider.Value:0} px";
        QualityLabel.Text = $"质量：{QualitySlider.Value:0}（越小越清晰）";
    }

    private void PushToState()
    {
        if (!_ready || _state is null || _syncing) return;
        _syncing = true;
        try
        {
            _state.Title = TitleBox.Text.Trim(); _state.Artist = ArtistBox.Text.Trim(); _state.Album = AlbumBox.Text.Trim();
            _state.FontFamily = string.IsNullOrWhiteSpace(FontFamilyCombo.Text) ? "Microsoft YaHei UI" : FontFamilyCombo.Text.Trim();
            _state.Fps = (int)FpsSlider.Value; _state.ArtworkScale = ArtworkScaleSlider.Value;
            _state.FlowSpeed = FlowSlider.Value; _state.RenderScale = RenderScaleSlider.Value;
            _state.FontScale = FontScaleSlider.Value; _state.FontWeight = Selected<int>(FontWeightCombo, 600);
            _state.SecondaryMode = Selected<string>(SecondaryModeCombo, RenderContracts.DefaultSecondaryMode);
            _state.ShowSongInfo = ShowSongInfoToggle.IsOn; _state.ShowArtwork = ShowArtworkToggle.IsOn;
            _state.ShowArtist = ShowArtistToggle.IsOn; _state.ShowAlbum = ShowAlbumToggle.IsOn;
            _state.ShowControls = ShowControlsToggle.IsOn; _state.LyricAlign = Selected<string>(LyricAlignCombo, "left");
            _state.LyricOffset = LyricOffsetSlider.Value; _state.LyricWidth = LyricWidthSlider.Value;
            _state.LyricFade = LyricFadeSlider.Value;
            _state.BackgroundDim = BackgroundDimSlider.Value; _state.BackgroundSaturation = BackgroundSaturationSlider.Value;
            _state.BackgroundBlur = BackgroundBlurSlider.Value; _state.Quality = (int)QualitySlider.Value;
            _state.Tail = double.IsNaN(TailBox.Value) ? 1200 : (int)TailBox.Value;
            _state.AudioCodec = Selected<string>(AudioCodecCombo, RenderContracts.DefaultAudioCodec);
            _state.AudioBitrate = Selected<string>(AudioBitrateCombo, "320k");
            _state.BrowserGpu = Selected<string>(BrowserGpuCombo, RenderContracts.DefaultBrowserGpu);
            if (PresetCombo.SelectedItem is StagePreset preset) _state.Preset = preset.Id;
            if (EncoderCombo.SelectedItem is EncoderOption encoder && encoder.IsAvailable)
            {
                _state.Encoder = encoder.Id;
                _state.EncoderPreset = encoder.Id == "auto"
                    ? ""
                    : Selected<string>(EncoderPresetCombo,
                        RenderContracts.GetDefaultEncoderPreset(encoder.Id, encoder.UsesModernNvencPresets));
            }
        }
        finally { _syncing = false; }
    }

    private static T Selected<T>(ComboBox combo, T fallback) => combo.SelectedItem is T value ? value : fallback;
    private static void SelectValue<T>(ComboBox combo, T value) => combo.SelectedItem = value;
    private static void SelectItem(ComboBox combo, object value) => combo.SelectedItem = value;

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_state is null || _runner is null) return;
        if (!_state.HasAudio || !File.Exists(_state.AudioPath))
        {
            ShowInfo("请先选择有效的音频文件。", InfoBarSeverity.Warning); return;
        }
        if (_runner.IsRunning) { ShowInfo("已有渲染任务正在运行。", InfoBarSeverity.Warning); return; }
        if (EncoderCombo.SelectedItem is not EncoderOption { IsAvailable: true })
        {
            ShowInfo("请选择可用的视频编码器。", InfoBarSeverity.Warning); return;
        }
        PushToState();
        if (!HasRequiredTitle(_state.Title))
        {
            ShowInfo("歌曲标题为必填项；艺术家、专辑、歌词和封面均可留空。", InfoBarSeverity.Warning); return;
        }
        string? outputPath = string.IsNullOrWhiteSpace(OutputBox.Text) ? null : OutputBox.Text.Trim();
        string? compatibilityError = RenderContracts.GetAudioOutputError(outputPath, _state.AudioCodec, _state.Tail);
        if (compatibilityError is not null)
        {
            ShowInfo(compatibilityError, InfoBarSeverity.Warning); return;
        }
        if (outputPath is not null)
        {
            string? conflict = GetOutputConflict(outputPath, _state);
            if (conflict is not null) { ShowInfo(conflict, InfoBarSeverity.Warning); return; }
            if (File.Exists(outputPath) && !string.Equals(_overwriteConfirmedPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                // ffmpeg 以 -y 无条件覆盖：首次点击先提示，再次点击才真正覆盖
                _overwriteConfirmedPath = outputPath;
                ShowInfo($"输出文件已存在：{Path.GetFileName(outputPath)}。再次点击「开始渲染」将覆盖该文件。", InfoBarSeverity.Warning);
                return;
            }
        }
        _overwriteConfirmedPath = null;
        if (_state.AudioCodec == "copy")
            ShowInfo(RenderContracts.AudioCopyOffsetWarning, InfoBarSeverity.Informational);
        _log.Clear(); LogText.Text = ""; Progress.Value = 0; ProgressDetailText.Text = "";
        _lastOutputPath = null; OpenOutputButton.IsEnabled = false; PlayResultButton.IsEnabled = false;
        var request = CreateRequest(_state, outputPath);
        try
        {
            _runner.Start(request); StartButton.IsEnabled = false; CancelButton.IsEnabled = true;
            SetStatus("正在启动渲染…", "启动");
            if (_state.AudioCodec != "copy") TaskInfoBar.IsOpen = false;
        }
        catch (Exception ex) { ShowInfo($"启动失败：{ex.Message}", InfoBarSeverity.Error); }
    }

    internal static bool HasRequiredTitle(string? title) => AppState.IsTitleValid(title);

    internal static RenderRequest CreateRequest(AppState state, string? outputPath)
    {
        string encoder = RenderContracts.NormalizeEncoder(state.Encoder);
        string encoderPreset = encoder == "auto"
            ? ""
            : RenderContracts.IsKnownEncoderPreset(encoder, state.EncoderPreset)
                ? state.EncoderPreset
                : RenderContracts.GetDefaultEncoderPreset(encoder);
        return new RenderRequest
        {
            AudioPath = state.AudioPath!, LyricPath = state.LyricPath, CoverPath = state.CoverPath, OutputPath = outputPath,
            Title = state.Title, Artist = state.Artist, Album = state.Album, FontFamily = state.FontFamily,
            FontScale = state.FontScale, FontWeight = state.FontWeight, SecondaryMode = state.SecondaryMode,
            ShowSongInfo = state.ShowSongInfo, ShowArtist = state.ShowArtist, ShowAlbum = state.ShowAlbum,
            ShowArtwork = state.ShowArtwork, ShowControls = state.ShowControls,
            ArtworkScale = state.ArtworkScale, LyricAlign = state.LyricAlign, LyricOffset = state.LyricOffset,
            LyricWidth = state.LyricWidth, LyricFade = state.LyricFade, BackgroundDim = state.BackgroundDim,
            BackgroundSaturation = state.BackgroundSaturation, BackgroundBlur = state.BackgroundBlur,
            FlowSpeed = state.FlowSpeed, RenderScale = state.RenderScale, Preset = state.Preset, Fps = state.Fps,
            Tail = state.Tail, Encoder = encoder, EncoderPreset = encoderPreset, Quality = state.Quality,
            AudioCodec = state.AudioCodec, AudioBitrate = state.AudioBitrate, BrowserGpu = state.BrowserGpu,
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e) { _runner?.Cancel(); SetStatus("正在取消…", "取消"); }

    /// <summary>ffmpeg 以 -y 无条件覆盖输出；输入/输出同路径会同时读写损坏源文件，必须在启动前拦截。</summary>
    private static string? GetOutputConflict(string outputPath, AppState state)
    {
        string fullOutput;
        try { fullOutput = Path.GetFullPath(outputPath); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"输出路径无效：{ex.Message}";
        }
        foreach ((string? path, string label) in new[] { (state.AudioPath, "音频"), (state.LyricPath, "歌词"), (state.CoverPath, "封面") })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); } catch { continue; }
            if (string.Equals(fullPath, fullOutput, StringComparison.OrdinalIgnoreCase))
                return $"输出文件不能与{label}文件是同一个文件：{fullPath}";
        }
        return null;
    }

    private void OnRenderProgress(object? sender, RenderProgressEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        switch (args.Stage)
        {
            case "server": SetStatus("正在加载页面…", "本地服务"); Progress.Value = 1; break;
            case "ready":
                SetStatus("素材已就绪", "准备抓帧"); Progress.Value = 2;
                ProgressDetailText.Text = $"音频时长 {args.TotalMs / 1000.0:0.0} s · 歌词 {args.LyricLines} 行"; break;
            case "capture":
                SetStatus("正在逐帧抓取", "抓帧"); Progress.Value = Math.Clamp(args.Percent, 0, 99);
                ProgressDetailText.Text = $"{args.Frames} 帧 · {args.ElapsedMs / 1000.0:0.0} s / {args.TotalMs / 1000.0:0.0} s"; break;
            case "encode": SetStatus("正在编码与合成音轨", "编码"); Progress.Value = 99; break;
            case "done":
                Progress.Value = 100; _lastOutputPath = args.OutputPath; SetStatus("渲染完成", "完成");
                ProgressDetailText.Text = $"{args.OutputPath} · {args.Bytes / 1024.0 / 1024.0:0.0} MB · {args.Frames} 帧";
                OpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(args.OutputPath);
                PlayResultButton.IsEnabled = !string.IsNullOrWhiteSpace(args.OutputPath);
                ShowInfo("成片已成功导出。", InfoBarSeverity.Success); break;
            case "error": SetStatus("渲染失败", "错误"); AppendLog("错误：" + args.Error); ShowInfo(args.Error ?? "渲染失败。", InfoBarSeverity.Error); break;
        }
    });

    private void OnRenderLog(object? sender, string line) => DispatcherQueue.TryEnqueue(() => AppendLog(line));

    private void OnRenderCompleted(object? sender, bool success) => DispatcherQueue.TryEnqueue(() =>
    {
        StartButton.IsEnabled = true; CancelButton.IsEnabled = false;
        if (!success && Progress.Value < 100)
        {
            SetStatus("渲染中断或失败", "已结束");
            ShowInfo("任务未成功完成，请查看日志。", InfoBarSeverity.Error);
        }
    });

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        // 环形截断：避免长任务高频日志时每条都全量拷贝 StringBuilder 导致 UI 线程卡顿
        if (_log.Length > 200_000)
        {
            string retained = _log.ToString(100_000, _log.Length - 100_000);
            _log.Clear().Append(retained);
        }
        LogText.Text = _log.ToString();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
    }

    private void SetStatus(string status, string stage) { StatusText.Text = status; StageText.Text = $"阶段：{stage}"; }
    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        TaskInfoBar.Message = message; TaskInfoBar.Severity = severity; TaskInfoBar.IsOpen = true;
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath) && !ShellReveal.RevealInExplorer(_lastOutputPath))
            ShowInfo("无法在资源管理器中定位输出文件。", InfoBarSeverity.Warning);
    }

    private async void OnPlayResult(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath) && !await ShellReveal.OpenMediaFileAsync(_lastOutputPath))
            ShowInfo("无法使用默认播放器打开成片。", InfoBarSeverity.Warning);
    }

    private async void OnPickAudio(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync(AudioExtensions, PickerLocationId.MusicLibrary);
        if (path is not null && _state is not null) _state.AudioPath = path;
    }

    private async void OnPickLyric(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync(LyricExtensions, PickerLocationId.DocumentsLibrary);
        if (path is not null && _state is not null) _state.LyricPath = path;
    }

    private async void OnPickCover(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync(CoverExtensions, PickerLocationId.PicturesLibrary);
        if (path is not null && _state is not null) _state.CoverPath = path;
    }

    private async void OnPickOutput(object sender, RoutedEventArgs e)
    {
        if (WindowHandle == nint.Zero) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, SuggestedFileName = "amll-mv" };
        picker.FileTypeChoices.Add("MP4 视频", [".mp4"]); picker.DefaultFileExtension = ".mp4";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file is not null) OutputBox.Text = file.Path;
    }

    private async Task<string?> PickFileAsync(IReadOnlyCollection<string> extensions, PickerLocationId location)
    {
        if (WindowHandle == nint.Zero) return null;
        var picker = new FileOpenPicker { SuggestedStartLocation = location };
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file is not null && extensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase)
            && File.Exists(file.Path) ? file.Path : null;
    }
}
