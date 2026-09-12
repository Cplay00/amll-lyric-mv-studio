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
    private string? _overwriteConfirmedKey;
    private CancellationTokenSource? _lifecycleCts;
    private Task? _encoderProbeTask;
    private readonly StringBuilder _log = new();
    private bool _syncing = true;
    private bool _ready;
    private bool _stackedLayout;

    internal const double StackLayoutBreakpoint = GuiLayoutMetrics.StackLayoutBreakpoint;
    internal const double ButtonStackBreakpoint = GuiLayoutMetrics.CompactLayoutBreakpoint;
    internal const double CategoryStackBreakpoint = GuiLayoutMetrics.CategoryLayoutBreakpoint;
    internal const double ParameterWidthRatio = GuiLayoutMetrics.ParameterWidthRatio;
    internal const double ParameterMaxWidth = GuiLayoutMetrics.ParameterMaxWidth;

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
        ParameterPanel.MaxWidth = ParameterMaxWidth;
        PageScrollViewer.SizeChanged += OnPageViewportSizeChanged;
        EnsureRowCount(ProjectInfoGrid, 3);
        EnsureRowCount(StageLayoutGrid, 5);
        EnsureRowCount(LyricsGrid, 9);
        EnsureRowCount(BackgroundGrid, 3);
        EnsureRowCount(DisplayOptionsGrid, 3);
        EnsureRowCount(EncodingGrid, 9);
        PresetCombo.DisplayMemberPath = nameof(StagePreset.DisplayLabel);
        FontFamilyCombo.ItemsSource = SystemFontCatalog.GetFontFamilies();
        FontWeightCombo.ItemsSource = Enumerable.Range(1, 9).Select(value => value * 100).ToArray();
        SecondaryModeCombo.ItemsSource = RenderContracts.SecondaryModes;
        LyricAlignCombo.ItemsSource = new[] { "left", "center", "right" };
        BrowserGpuCombo.ItemsSource = RenderContracts.BrowserGpuModes;
        AudioCodecCombo.ItemsSource = RenderContracts.AudioCodecs;
        AudioBitrateCombo.ItemsSource = new[] { "128k", "192k", "256k", "320k" };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>应用退出时同步取消渲染进程树（Kill 是同步语义），防止遗留孤儿 node/Chromium/ffmpeg。</summary>
    public void Shutdown()
    {
        _lifecycleCts?.Cancel();
        _runner?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyViewportHeight();
        if (!_ready || (_encoderProbeTask is { IsCompleted: false })) return;
        StartEncoderProbe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lifecycleCts?.Cancel();
        _runner?.Cancel();
    }

    public nint WindowHandle { get; set; }

    private void OnExportLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        bool stacked = ShouldStackLayout(width);
        if (stacked != _stackedLayout)
        {
            _stackedLayout = stacked;
            Grid.SetRow(TaskPane, stacked ? 1 : 0);
            Grid.SetColumn(TaskPane, stacked ? 0 : 1);
            ConfigurationColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(3, GridUnitType.Star);
            TaskColumn.Width = stacked ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            // 宽屏：内容行占满视口高度，任务面板随之拉伸；窄屏：堆叠时恢复自动高度与有界面板。
            MainLayout.RowDefinitions[0].Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            TaskPane.MaxHeight = stacked ? StackedTaskPaneMaxHeight : double.NaN;
            LogScroll.MaxHeight = stacked ? StackedLogMaxHeight : double.NaN;
            ParameterPanel.MaxWidth = stacked ? ParameterMaxWidth : double.NaN;
            ApplyViewportHeight();
        }
        ParameterPanel.Width = stacked ? GetParameterPanelWidth(width, stacked) : double.NaN;
        ParameterPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        bool stackCategories = ShouldStackCategoryLayout(ParameterPanel.Width);
        ApplyCategoryLayouts(stackCategories);

        bool stackButtons = ShouldStackButtons(width);
        SetButtonLayout(PrimaryTaskButtons, PrimaryTaskSecondColumn, CancelButton, stackButtons);
        SetButtonLayout(ResultTaskButtons, ResultTaskSecondColumn, PlayResultButton, stackButtons);
    }

    internal const double StackedTaskPaneMaxHeight = 820;
    internal const double StackedLogMaxHeight = 300;

    private void OnPageViewportSizeChanged(object sender, SizeChangedEventArgs e) => ApplyViewportHeight();

    /// <summary>
    /// 宽屏把 MainLayout 高度钳制到滚动视口（下限 480：过矮窗口回退整页滚动），
    /// 右列任务面板因此与左列总高度平齐、日志占满剩余空间；窄屏恢复自动高度交给外层滚动。
    /// </summary>
    private void ApplyViewportHeight()
    {
        double target = _stackedLayout || PageScrollViewer.ViewportHeight <= 0
            ? double.NaN
            : Math.Max(PageScrollViewer.ViewportHeight, 480);
        bool changed = double.IsNaN(target) != double.IsNaN(MainLayout.Height)
            || (!double.IsNaN(target) && Math.Abs(target - MainLayout.Height) > 0.5);
        if (changed) MainLayout.Height = target;
    }

    internal static bool ShouldStackLayout(double width) => GuiLayoutMetrics.ShouldStackMainLayout(width);
    internal static bool ShouldStackButtons(double width) => GuiLayoutMetrics.ShouldUseCompactLayout(width);
    internal static bool ShouldStackCategoryLayout(double width) =>
        width < GuiLayoutMetrics.CategoryLayoutBreakpoint;

    internal static double GetParameterPanelWidth(double width, bool stacked) =>
        GuiLayoutMetrics.GetParameterPanelWidth(width, stacked);

    private static void SetButtonLayout(Grid panel, ColumnDefinition secondColumn, FrameworkElement secondButton, bool stacked)
    {
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondButton, stacked ? 1 : 0);
        Grid.SetColumn(secondButton, stacked ? 0 : 1);
    }

    private void ApplyCategoryLayouts(bool stacked)
    {
        SetGridColumns(ProjectInfoGrid, stacked);
        Place(TitleBox, 0, 0, stacked ? 1 : 2);
        Place(ArtistBox, 1, 0);
        Place(AlbumBox, stacked ? 2 : 1, stacked ? 0 : 1);

        SetGridColumns(StageLayoutGrid, stacked);
        Place(PresetCombo, 0, 0, stacked ? 1 : 2);
        Place(FpsSlider.Parent as FrameworkElement, 1, 0);
        Place(ArtworkScaleSlider.Parent as FrameworkElement, stacked ? 2 : 1, stacked ? 0 : 1);
        Place(FlowSlider.Parent as FrameworkElement, stacked ? 3 : 2, 0);
        Place(RenderScaleSlider.Parent as FrameworkElement, stacked ? 4 : 2, stacked ? 0 : 1);

        SetGridColumns(LyricsGrid, stacked);
        Place(FontFamilyCombo, 0, 0, stacked ? 1 : 2);
        Place(FontScaleSlider.Parent as FrameworkElement, 1, 0);
        Place(FontWeightCombo, stacked ? 2 : 1, stacked ? 0 : 1);
        Place(SecondaryModeCombo, stacked ? 3 : 2, 0);
        Place(LyricAlignCombo, stacked ? 4 : 2, stacked ? 0 : 1);
        Place(LyricOffsetSlider.Parent as FrameworkElement, stacked ? 5 : 3, 0);
        Place(LyricWidthSlider.Parent as FrameworkElement, stacked ? 6 : 3, stacked ? 0 : 1);
        Place(LyricFadeSlider.Parent as FrameworkElement, stacked ? 7 : 4, 0, stacked ? 1 : 2);

        SetGridColumns(BackgroundGrid, stacked);
        Place(BackgroundDimSlider.Parent as FrameworkElement, 0, 0);
        Place(BackgroundSaturationSlider.Parent as FrameworkElement, stacked ? 1 : 0, stacked ? 0 : 1);
        Place(BackgroundBlurSlider.Parent as FrameworkElement, stacked ? 2 : 1, 0, stacked ? 1 : 2);

        SetGridColumns(DisplayOptionsGrid, stacked);
        Place(ShowSongInfoToggle.Parent as FrameworkElement, 0, 0);
        Place(ShowControlsToggle.Parent as FrameworkElement, stacked ? 1 : 0, stacked ? 0 : 1);
        Place(ShowArtistToggle.Parent as FrameworkElement, stacked ? 2 : 1, 0);

        SetGridColumns(EncodingGrid, stacked);
        Place(OutputBox.Parent as FrameworkElement, 0, 0, stacked ? 1 : 2);
        Place(EncoderCombo, 1, 0);
        Place(EncoderPresetCombo, stacked ? 2 : 1, stacked ? 0 : 1);
        Place(QualitySlider.Parent as FrameworkElement, stacked ? 3 : 2, 0);
        Place(HeadBox, stacked ? 4 : 2, stacked ? 0 : 1);
        Place(AudioCodecCombo, stacked ? 5 : 3, 0);
        Place(TailBox, stacked ? 6 : 3, stacked ? 0 : 1);
        Place(AudioBitrateCombo, stacked ? 7 : 4, 0);
        Place(EncodingGrid.Children.OfType<TextBlock>().LastOrDefault(), stacked ? 8 : 4, stacked ? 0 : 1);
    }

    private static void SetGridColumns(Grid grid, bool stacked)
    {
        if (grid.ColumnDefinitions.Count < 2) return;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private static void Place(FrameworkElement? element, int row, int column, int columnSpan = 1)
    {
        if (element is null) return;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private static void EnsureRowCount(Grid grid, int count)
    {
        while (grid.RowDefinitions.Count < count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

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
        StartEncoderProbe();
    }

    private void StartEncoderProbe()
    {
        _lifecycleCts?.Cancel();
        _lifecycleCts?.Dispose();
        _lifecycleCts = new CancellationTokenSource();
        _encoderProbeTask = LoadEncodersAsync(_lifecycleCts.Token);
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
        QualitySlider.Value = _state.Quality; HeadBox.Value = _state.Head; TailBox.Value = _state.Tail;
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

    private async Task LoadEncodersAsync(CancellationToken cancellationToken)
    {
        if (_runner is null || _state is null) return;
        try
        {
            IReadOnlyList<EncoderOption> options = await _runner.ProbeEncodersAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsLoaded) return;
            EncoderCombo.ItemsSource = options;
            EncoderCombo.DisplayMemberPath = nameof(EncoderOption.DisplayLabel);
            EncoderOption? selected = options.FirstOrDefault(x => x.IsAvailable && x.Id == _state.Encoder)
                ?? options.FirstOrDefault(x => x.IsAvailable && x.Id == "auto")
                ?? options.FirstOrDefault(x => x.IsAvailable);
            EncoderCombo.SelectedItem = selected;
            if (selected is not null)
            {
                _state.Encoder = selected.Id;
                UpdateEncoderPresets(selected, preserveCurrent: true);
            }
            if (options.All(x => !x.IsAvailable))
            {
                string reason = options.Select(x => x.FailureReason).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                    ?? "未探测到可用的视频编码器。";
                ShowInfo(reason, InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 页面卸载后的预期取消，不再更新 UI。
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && IsLoaded)
                ShowInfo($"编码器探测失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || !_ready) return;
        if (e.PropertyName is nameof(AppState.AudioPath) or nameof(AppState.LyricPath) or nameof(AppState.CoverPath))
        {
            _overwriteConfirmedKey = null;
            SyncAssetBadges();
        }
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
            UpdateEncoderPresets(encoder, preserveCurrent: false);
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

    private void UpdateEncoderPresets(EncoderOption option, bool preserveCurrent)
    {
        string current = preserveCurrent ? _state?.EncoderPreset ?? "" : "";
        IReadOnlyList<string> presets = option.Id == "auto" ? Array.Empty<string>() : option.SupportedPresets;
        EncoderPresetCombo.ItemsSource = presets;
        EncoderPresetCombo.IsEnabled = presets.Count > 0;
        string selected = presets.Contains(current, StringComparer.Ordinal)
            ? current
            : presets.Contains(option.DefaultPreset ?? "", StringComparer.Ordinal)
                ? option.DefaultPreset!
                : presets.FirstOrDefault() ?? "";
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
            _state.Head = double.IsNaN(HeadBox.Value) ? 0 : (int)HeadBox.Value;
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
                    : Selected<string>(EncoderPresetCombo, encoder.DefaultPreset ?? "");
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
        if (EncoderCombo.SelectedItem is not EncoderOption { IsAvailable: true } selectedEncoder)
        {
            ShowInfo("请选择可用的视频编码器。", InfoBarSeverity.Warning); return;
        }
        PushToState();
        if (!HasRequiredTitle(_state.Title))
        {
            ShowInfo("歌曲标题为必填项；艺术家、专辑、歌词和封面均可留空。", InfoBarSeverity.Warning); return;
        }
        StagePreset canvas = StagePresets.Resolve(_state.Preset);
        if (!RenderContracts.TryResolveGuiOutputPath(
            OutputBox.Text, _state.ProjectRoot, _state.AudioPath!, canvas.Width, canvas.Height, _state.Fps,
            out string? outputPath, out string? outputError))
        {
            _overwriteConfirmedKey = null;
            ShowInfo(outputError ?? "输出路径无效。", InfoBarSeverity.Warning); return;
        }
        string? compatibilityError = RenderContracts.GetAudioOutputError(
            outputPath!, _state.AudioCodec, _state.Head, _state.Tail);
        if (compatibilityError is not null)
        {
            ShowInfo(compatibilityError, InfoBarSeverity.Warning); return;
        }
        string? conflict = GetOutputConflict(outputPath!, _state);
        if (conflict is not null) { ShowInfo(conflict, InfoBarSeverity.Warning); return; }
        string canonicalOutput = Path.TrimEndingDirectorySeparator(outputPath!);
        string overwriteKey = BuildOverwriteKey(canonicalOutput, _state);
        if (File.Exists(canonicalOutput) && !string.Equals(_overwriteConfirmedKey, overwriteKey, StringComparison.Ordinal))
        {
            // ffmpeg 以 -y 无条件覆盖：首次点击先提示，再次点击才真正覆盖；
            // key 绑定 canonical path 与关键配置，输出文本/编解码/时长/画布变化后不会误沿用旧确认
            _overwriteConfirmedKey = overwriteKey;
            ShowInfo($"输出文件已存在：{Path.GetFileName(canonicalOutput)}。再次点击「开始渲染」将覆盖该文件。", InfoBarSeverity.Warning);
            return;
        }
        _overwriteConfirmedKey = null;
        if (_state.AudioCodec == "copy")
            ShowInfo(RenderContracts.AudioCopyOffsetWarning, InfoBarSeverity.Informational);
        _log.Clear(); LogText.Text = ""; Progress.Value = 0; ProgressDetailText.Text = "";
        _lastOutputPath = null; OpenOutputButton.IsEnabled = false; PlayResultButton.IsEnabled = false;
        var request = CreateRequest(_state, outputPath!);
        try
        {
            _runner.Start(request, selectedEncoder.SupportedPresets); StartButton.IsEnabled = false; CancelButton.IsEnabled = true;
            SetStatus("正在启动渲染…", "启动");
            if (_state.AudioCodec != "copy") TaskInfoBar.IsOpen = false;
        }
        catch (Exception ex) { ShowInfo($"启动失败：{ex.Message}", InfoBarSeverity.Error); }
    }

    internal static bool HasRequiredTitle(string? title) => AppState.IsTitleValid(title);

    internal static RenderRequest CreateRequest(AppState state, string outputPath)
    {
        string encoder = RenderContracts.NormalizeEncoder(state.Encoder);
        string encoderPreset = encoder == "auto" ? "" : state.EncoderPreset;
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
            Tail = state.Tail, Head = state.Head, Encoder = encoder, EncoderPreset = encoderPreset, Quality = state.Quality,
            AudioCodec = state.AudioCodec, AudioBitrate = state.AudioBitrate, BrowserGpu = state.BrowserGpu,
        };
    }

    private void OnOutputTextChanged(object sender, TextChangedEventArgs e) => _overwriteConfirmedKey = null;

    /// <summary>覆盖确认绑定 canonical 输出路径与影响输出的关键配置；任一变化即要求重新确认。</summary>
    private static string BuildOverwriteKey(string canonicalOutput, AppState state) =>
        string.Join("|", canonicalOutput, state.AudioCodec, state.Head, state.Tail,
            StagePresets.Resolve(state.Preset).Id, state.Fps, state.AudioPath ?? "");

    private void OnCancel(object sender, RoutedEventArgs e) { _runner?.Cancel(); SetStatus("正在取消…", "取消"); }

    /// <summary>ffmpeg 以 -y 无条件覆盖输出；输入/输出同路径会同时读写损坏源文件，必须在启动前拦截。</summary>
    private static string? GetOutputConflict(string outputPath, AppState state)
    {
        if (!Path.IsPathFullyQualified(outputPath)) return "输出路径必须是绝对路径。";
        string fullOutput = Path.TrimEndingDirectorySeparator(outputPath);
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

    private async Task ApplyPickedAssetAsync(Func<string?> getCurrentPath, Action<string> setPath, IReadOnlyCollection<string> extensions, PickerLocationId location)
    {
        string? path = await PickFileAsync(extensions, location);
        if (path is null || _state is null) return;
        if (string.Equals(getCurrentPath(), path, StringComparison.OrdinalIgnoreCase)) _state.NotifyAssetReselected(path);
        else setPath(path);
    }

    private async void OnPickAudio(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.AudioPath, value => _state!.AudioPath = value, AudioExtensions, PickerLocationId.MusicLibrary);

    private async void OnPickLyric(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.LyricPath, value => _state!.LyricPath = value, LyricExtensions, PickerLocationId.DocumentsLibrary);

    private async void OnPickCover(object sender, RoutedEventArgs e) =>
        await ApplyPickedAssetAsync(() => _state?.CoverPath, value => _state!.CoverPath = value, CoverExtensions, PickerLocationId.PicturesLibrary);

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
