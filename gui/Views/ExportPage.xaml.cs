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
    private string? _autoFilledTitle;
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

    /// <summary>本地化的预设显示项；DisplayLabel 跟随语言，custom 的尺寸跟随用户输入。</summary>
    public sealed record PresetOption(StagePreset Preset)
    {
        public string DisplayLabel { get; } =
            $"{LocalizationService.T($"Preset.{Preset.Id}")} · {Preset.Dimensions}";
    }

    private List<PresetOption> BuildPresetOptions()
    {
        int customWidth = _state?.CustomStageWidth ?? StagePresets.Resolve(StagePresets.CustomId).Width;
        int customHeight = _state?.CustomStageHeight ?? StagePresets.Resolve(StagePresets.CustomId).Height;
        return StagePresets.All
            .Select(preset => new PresetOption(
                preset.Id == StagePresets.CustomId ? preset with { Width = customWidth, Height = customHeight } : preset))
            .ToList();
    }

    public ExportPage()
    {
        InitializeComponent();
        PresetCombo.ItemsSource = BuildPresetOptions();
        FpsSlider.Minimum = 24;
        FpsSlider.Maximum = 120;
        FpsSlider.StepFrequency = 1;
        ArtworkScaleSlider.Minimum = 0.5;
        ArtworkScaleSlider.Maximum = 1.5;
        RenderScaleSlider.Minimum = 0.25;
        RenderScaleSlider.Maximum = 1;
        FontScaleSlider.Minimum = 0.5;
        FontScaleSlider.Maximum = 2;
        LyricWidthSlider.Minimum = RenderContracts.MinLyricWidth;
        LyricWidthSlider.Maximum = RenderContracts.MaxLyricWidth;
        LyricHeightSlider.Minimum = RenderContracts.MinLyricHeight;
        LyricHeightSlider.Maximum = RenderContracts.MaxLyricHeight;
        InfoOffsetXSlider.Minimum = RenderContracts.MinInfoOffset;
        InfoOffsetXSlider.Maximum = RenderContracts.MaxInfoOffset;
        InfoOffsetYSlider.Minimum = RenderContracts.MinInfoOffset;
        InfoOffsetYSlider.Maximum = RenderContracts.MaxInfoOffset;
        LyricFadeSlider.Minimum = RenderContracts.MinLyricFade;
        LyricFadeSlider.Maximum = RenderContracts.MaxLyricFade;
        QualitySlider.Minimum = 10;
        QualitySlider.Maximum = 30;
        ParameterPanel.MaxWidth = ParameterMaxWidth;
        PageScrollViewer.SizeChanged += OnPageViewportSizeChanged;
        EnsureRowCount(ProjectInfoGrid, 3);
        EnsureRowCount(StageLayoutGrid, 6);
        EnsureRowCount(LyricsGrid, 11);
        EnsureRowCount(BackgroundGrid, 3);
        EnsureRowCount(DisplayOptionsGrid, 3);
        EnsureRowCount(EncodingGrid, 9);
        PresetCombo.DisplayMemberPath = nameof(PresetOption.DisplayLabel);
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
        Place(CustomSizePanel, 1, 0, stacked ? 1 : 2);
        Place(FpsSlider.Parent as FrameworkElement, 2, 0);
        Place(ArtworkScaleSlider.Parent as FrameworkElement, stacked ? 3 : 2, stacked ? 0 : 1);
        Place(FlowSlider.Parent as FrameworkElement, stacked ? 4 : 3, 0);
        Place(RenderScaleSlider.Parent as FrameworkElement, stacked ? 5 : 3, stacked ? 0 : 1);

        SetGridColumns(LyricsGrid, stacked);
        Place(FontFamilyCombo, 0, 0, stacked ? 1 : 2);
        Place(FontScaleSlider.Parent as FrameworkElement, 1, 0);
        Place(FontWeightCombo, stacked ? 2 : 1, stacked ? 0 : 1);
        Place(SecondaryModeCombo, stacked ? 3 : 2, 0);
        Place(LyricAlignCombo, stacked ? 4 : 2, stacked ? 0 : 1);
        Place(LyricOffsetSlider.Parent as FrameworkElement, stacked ? 5 : 3, 0);
        Place(LyricWidthSlider.Parent as FrameworkElement, stacked ? 6 : 3, stacked ? 0 : 1);
        Place(LyricHeightSlider.Parent as FrameworkElement, stacked ? 7 : 4, 0);
        Place(InfoOffsetXSlider.Parent as FrameworkElement, stacked ? 8 : 4, stacked ? 0 : 1);
        Place(InfoOffsetYSlider.Parent as FrameworkElement, stacked ? 9 : 5, 0);
        Place(LyricFadeSlider.Parent as FrameworkElement, stacked ? 10 : 5, stacked ? 0 : 1);

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

    public void ApplyLocalization()
    {
        PageTitleText.Text = LocalizationService.T("Export.Title");
        PageSubtitleText.Text = LocalizationService.T("Export.Subtitle");
        ProjectInfoExpander.Header = LocalizationService.T("Export.ProjectInfo");
        TitleBox.Header = LocalizationService.T("Export.TitleHeader");
        TitleBox.PlaceholderText = LocalizationService.T("Export.TitlePlaceholder");
        ArtistBox.Header = LocalizationService.T("Export.ArtistHeader");
        ArtistBox.PlaceholderText = LocalizationService.T("Export.ArtistPlaceholder");
        AlbumBox.Header = LocalizationService.T("Export.AlbumHeader");
        AlbumBox.PlaceholderText = LocalizationService.T("Export.AlbumPlaceholder");
        AssetsExpander.Header = LocalizationService.T("Export.Assets");
        AudioButtonText.Text = LocalizationService.T("Export.AudioRequired");
        ToolTipService.SetToolTip(PickAudioButton, LocalizationService.T("Export.AudioTooltip"));
        LyricButtonText.Text = LocalizationService.T("Export.LyricOptional");
        ToolTipService.SetToolTip(PickLyricButton, LocalizationService.T("Export.LyricTooltip"));
        CoverButtonText.Text = LocalizationService.T("Export.CoverOptional");
        ToolTipService.SetToolTip(PickCoverButton, LocalizationService.T("Export.CoverTooltip"));
        StageLayoutExpander.Header = LocalizationService.T("Export.StageLayout");
        PresetCombo.Header = LocalizationService.T("Export.PresetHeader");
        CustomWidthBox.Header = LocalizationService.T("Export.CustomWidthHeader");
        CustomHeightBox.Header = LocalizationService.T("Export.CustomHeightHeader");
        LyricsExpander.Header = LocalizationService.T("Export.LyricsSection");
        FontFamilyCombo.Header = LocalizationService.T("Export.FontFamilyHeader");
        ToolTipService.SetToolTip(FontFamilyCombo, LocalizationService.T("Export.FontFamilyTooltip"));
        FontWeightCombo.Header = LocalizationService.T("Export.FontWeight");
        SecondaryModeCombo.Header = LocalizationService.T("Export.SecondaryMode");
        LyricAlignCombo.Header = LocalizationService.T("Export.LyricAlign");
        ToolTipService.SetToolTip(LyricHeightSlider, LocalizationService.T("Export.LyricHeightTooltip"));
        ToolTipService.SetToolTip(InfoOffsetXSlider, LocalizationService.T("Export.InfoOffsetTooltip"));
        ToolTipService.SetToolTip(InfoOffsetYSlider, LocalizationService.T("Export.InfoOffsetTooltip"));
        BackgroundExpander.Header = LocalizationService.T("Export.Background");
        DisplayOptionsExpander.Header = LocalizationService.T("Export.DisplayOptions");
        ShowSongInfoToggle.Header = LocalizationService.T("Export.ShowSongInfo");
        ToolTipService.SetToolTip(ShowSongInfoToggle, LocalizationService.T("Export.ShowSongInfoTooltip"));
        ShowArtworkToggle.Header = LocalizationService.T("Export.ShowArtwork");
        ShowControlsToggle.Header = LocalizationService.T("Export.ShowControls");
        ShowControlsToggle.OffContent = LocalizationService.T("Export.ShowControlsOff");
        ShowControlsToggle.OnContent = LocalizationService.T("Export.ShowControlsOn");
        ShowArtistToggle.Header = LocalizationService.T("Export.ShowArtist");
        ShowArtistToggle.OnContent = LocalizationService.T("Export.ShowArtistOn");
        ShowArtistToggle.OffContent = LocalizationService.T("Export.ShowArtistOff");
        ShowAlbumToggle.Header = LocalizationService.T("Export.ShowAlbum");
        ShowAlbumToggle.OnContent = LocalizationService.T("Export.ShowArtistOn");
        ShowAlbumToggle.OffContent = LocalizationService.T("Export.ShowArtistOff");
        BrowserGpuCombo.Header = LocalizationService.T("Export.BrowserGpu");
        ToolTipService.SetToolTip(BrowserGpuCombo, LocalizationService.T("Export.BrowserGpuTooltip"));
        EncodingExpander.Header = LocalizationService.T("Export.Encoding");
        OutputBox.Header = LocalizationService.T("Export.OutputHeader");
        OutputBox.PlaceholderText = LocalizationService.T("Export.OutputPlaceholder");
        ToolTipService.SetToolTip(PickOutputButton, LocalizationService.T("Export.PickOutputTooltip"));
        ToolTipService.SetToolTip(ShowControlsToggle, LocalizationService.T("Export.ShowControlsTooltip"));
        ToolTipService.SetToolTip(LyricFadeSlider, LocalizationService.T("Export.LyricFadeTooltip"));
        ToolTipService.SetToolTip(EncoderCombo, LocalizationService.T("Export.EncoderTooltip"));
        EncoderCombo.Header = LocalizationService.T("Export.Encoder");
        EncoderPresetCombo.Header = LocalizationService.T("Export.EncoderPreset");
        HeadBox.Header = LocalizationService.T("Export.Head");
        TailBox.Header = LocalizationService.T("Export.Tail");
        AudioCodecCombo.Header = LocalizationService.T("Export.AudioCodec");
        AudioBitrateCombo.Header = LocalizationService.T("Export.AudioBitrate");
        TimingNoteText.Text = LocalizationService.T("Export.TimingNote");
        TaskStatusTitle.Text = LocalizationService.T("Export.TaskStatus");
        TaskLogTitle.Text = LocalizationService.T("Export.TaskLog");
        StartButtonText.Text = LocalizationService.T("Export.StartRender");
        CancelButtonText.Text = LocalizationService.T("Export.CancelTask");
        OpenOutputButtonText.Text = LocalizationService.T("Export.OpenOutput");
        PlayResultButtonText.Text = LocalizationService.T("Export.PlayResult");
        SyncAssetBadges();
        UpdateLabels();
        RefreshPresetOptions();
        SetStatus(_lastStatusKey, _lastStageKey);
    }

    private void RefreshPresetOptions()
    {
        if (!_ready) return;
        PresetCombo.ItemsSource = BuildPresetOptions();
        if (_state is not null) SyncPresetSelection();
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
        LyricHeightSlider.Value = _state.LyricHeight;
        InfoOffsetXSlider.Value = _state.InfoOffsetX; InfoOffsetYSlider.Value = _state.InfoOffsetY;
        QualitySlider.Value = _state.Quality; HeadBox.Value = _state.Head; TailBox.Value = _state.Tail;
        CustomWidthBox.Value = _state.CustomStageWidth; CustomHeightBox.Value = _state.CustomStageHeight;
        ShowSongInfoToggle.IsOn = _state.ShowSongInfo; ShowArtworkToggle.IsOn = _state.ShowArtwork;
        ShowArtistToggle.IsOn = _state.ShowArtist; ShowAlbumToggle.IsOn = _state.ShowAlbum;
        ShowControlsToggle.IsOn = _state.ShowControls;
        LyricFadeSlider.Value = _state.LyricFade;
        SyncPresetSelection();
        SelectValue(FontWeightCombo, _state.FontWeight); SelectValue(SecondaryModeCombo, _state.SecondaryMode);
        SelectValue(LyricAlignCombo, _state.LyricAlign); SelectValue(BrowserGpuCombo, _state.BrowserGpu);
        SelectValue(AudioCodecCombo, _state.AudioCodec); SelectValue(AudioBitrateCombo, _state.AudioBitrate);
        SyncAssetBadges();
        SyncCustomSizeVisibility();
    }

    private void SyncPresetSelection()
    {
        if (_state is null) return;
        var options = (PresetCombo.ItemsSource as IEnumerable<PresetOption> ?? BuildPresetOptions()).ToList();
        PresetCombo.ItemsSource = options;
        PresetCombo.SelectedItem = options.FirstOrDefault(option => option.Preset.Id == _state.Preset);
    }

    private void SyncCustomSizeVisibility()
    {
        bool custom = _state is not null && _state.Preset == StagePresets.CustomId;
        CustomSizePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
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
                    ?? LocalizationService.T("Export.NoEncoder");
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
                ShowInfo(LocalizationService.T("Export.ProbeFailed", ex.Message), InfoBarSeverity.Warning);
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
        string notSelected = LocalizationService.T("Export.NotSelected");
        AudioBadge.Text = _state.HasAudio ? Path.GetFileName(_state.AudioPath) : notSelected;
        LyricBadge.Text = string.IsNullOrWhiteSpace(_state.LyricPath) ? notSelected : Path.GetFileName(_state.LyricPath);
        CoverBadge.Text = string.IsNullOrWhiteSpace(_state.CoverPath) ? notSelected : Path.GetFileName(_state.CoverPath);
    }

    private void OnConfigurationChanged(object sender, object e)
    {
        if (ReferenceEquals(sender, PresetCombo) && _ready)
        {
            // 直接读下拉选择而非 _state：PushToState 尚未把新预设写回 state。
            bool custom = PresetCombo.SelectedItem is PresetOption preset &&
                preset.Preset.Id == StagePresets.CustomId;
            CustomSizePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }
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

    private void OnFontPreviewTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text) text.Text = LocalizationService.T("Export.FontPreview");
    }

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

    private void OnNumberConfigurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        PushToState();
        if (ReferenceEquals(sender, CustomWidthBox) || ReferenceEquals(sender, CustomHeightBox))
        {
            // state 端规范化（取偶/越界回退）后把有效值回写到输入框，避免 UI 显示与实际生效尺寸不一致。
            if (_state is not null)
            {
                _syncing = true;
                try
                {
                    if (!double.IsNaN(CustomWidthBox.Value) && Math.Abs(CustomWidthBox.Value - _state.CustomStageWidth) > 0.5)
                        CustomWidthBox.Value = _state.CustomStageWidth;
                    if (!double.IsNaN(CustomHeightBox.Value) && Math.Abs(CustomHeightBox.Value - _state.CustomStageHeight) > 0.5)
                        CustomHeightBox.Value = _state.CustomStageHeight;
                }
                finally { _syncing = false; }
            }
            SyncPresetSelection();
        }
    }

    private void UpdateLabels()
    {
        if (!_ready && !_syncing) return;
        FpsLabel.Text = LocalizationService.T("Export.Fps", FpsSlider.Value);
        ArtworkScaleLabel.Text = LocalizationService.T("Export.ArtworkScale", ArtworkScaleSlider.Value * 100);
        FlowLabel.Text = LocalizationService.T("Export.Flow", FlowSlider.Value);
        RenderScaleLabel.Text = LocalizationService.T("Export.RenderScale", RenderScaleSlider.Value);
        FontScaleLabel.Text = LocalizationService.T("Export.FontScale", FontScaleSlider.Value * 100);
        LyricOffsetLabel.Text = LocalizationService.T("Export.LyricOffset", LyricOffsetSlider.Value);
        LyricWidthLabel.Text = LocalizationService.T("Export.LyricWidth", LyricWidthSlider.Value);
        LyricHeightLabel.Text = LocalizationService.T("Export.LyricHeight", LyricHeightSlider.Value);
        InfoOffsetXLabel.Text = LocalizationService.T("Export.InfoOffsetX", InfoOffsetXSlider.Value);
        InfoOffsetYLabel.Text = LocalizationService.T("Export.InfoOffsetY", InfoOffsetYSlider.Value);
        LyricFadeLabel.Text = LyricFadeSlider.Value <= 0
            ? LocalizationService.T("Export.LyricFadeOff")
            : LocalizationService.T("Export.LyricFade", LyricFadeSlider.Value.ToString("P0"));
        BackgroundDimLabel.Text = LocalizationService.T("Export.BackgroundDim", BackgroundDimSlider.Value * 100);
        BackgroundSaturationLabel.Text = LocalizationService.T("Export.BackgroundSaturation", BackgroundSaturationSlider.Value * 100);
        BackgroundBlurLabel.Text = LocalizationService.T("Export.BackgroundBlur", BackgroundBlurSlider.Value);
        QualityLabel.Text = LocalizationService.T("Export.Quality", QualitySlider.Value);
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
            _state.LyricHeight = LyricHeightSlider.Value;
            _state.InfoOffsetX = InfoOffsetXSlider.Value; _state.InfoOffsetY = InfoOffsetYSlider.Value;
            _state.LyricFade = LyricFadeSlider.Value;
            _state.BackgroundDim = BackgroundDimSlider.Value; _state.BackgroundSaturation = BackgroundSaturationSlider.Value;
            _state.BackgroundBlur = BackgroundBlurSlider.Value; _state.Quality = (int)QualitySlider.Value;
            _state.Head = double.IsNaN(HeadBox.Value) ? 0 : (int)HeadBox.Value;
            _state.Tail = double.IsNaN(TailBox.Value) ? 1200 : (int)TailBox.Value;
            _state.AudioCodec = Selected<string>(AudioCodecCombo, RenderContracts.DefaultAudioCodec);
            _state.AudioBitrate = Selected<string>(AudioBitrateCombo, "320k");
            _state.BrowserGpu = Selected<string>(BrowserGpuCombo, RenderContracts.DefaultBrowserGpu);
            if (PresetCombo.SelectedItem is PresetOption preset)
            {
                _state.Preset = preset.Preset.Id;
                if (preset.Preset.Id == StagePresets.CustomId)
                {
                    if (!double.IsNaN(CustomWidthBox.Value)) _state.CustomStageWidth = (int)CustomWidthBox.Value;
                    if (!double.IsNaN(CustomHeightBox.Value)) _state.CustomStageHeight = (int)CustomHeightBox.Value;
                }
            }
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

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_state is null || _runner is null) return;
        if (!_state.HasAudio || !File.Exists(_state.AudioPath))
        {
            ShowInfo(LocalizationService.T("Export.SelectAudioFirst"), InfoBarSeverity.Warning); return;
        }
        if (_runner.IsRunning) { ShowInfo(LocalizationService.T("Export.TaskRunning"), InfoBarSeverity.Warning); return; }
        if (EncoderCombo.SelectedItem is not EncoderOption { IsAvailable: true } selectedEncoder)
        {
            ShowInfo(LocalizationService.T("Export.SelectEncoder"), InfoBarSeverity.Warning); return;
        }
        PushToState();
        if (!HasRequiredTitle(_state.Title))
        {
            ShowInfo(LocalizationService.T("Export.TitleRequired"), InfoBarSeverity.Warning); return;
        }
        (int canvasWidth, int canvasHeight) = StagePresets.EffectiveDimensions(
            _state.Preset, _state.CustomStageWidth, _state.CustomStageHeight);
        if (!RenderContracts.TryResolveGuiOutputPath(
            OutputBox.Text, _state.ProjectRoot, _state.AudioPath!, canvasWidth, canvasHeight, _state.Fps,
            out string? outputPath, out string? outputError))
        {
            _overwriteConfirmedKey = null;
            ShowInfo(outputError ?? LocalizationService.T("Export.OutputInvalid"), InfoBarSeverity.Warning); return;
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
            ShowInfo(LocalizationService.T("Export.Overwrite", Path.GetFileName(canonicalOutput)), InfoBarSeverity.Warning);
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
            SetStatus("Export.StartingRender", "Export.StageBoot");
            if (_state.AudioCodec != "copy") TaskInfoBar.IsOpen = false;
        }
        catch (Exception ex) { ShowInfo(LocalizationService.T("Export.LaunchFailed", ex.Message), InfoBarSeverity.Error); }
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
            LyricWidth = state.LyricWidth, LyricHeight = state.LyricHeight,
            InfoOffsetX = state.InfoOffsetX, InfoOffsetY = state.InfoOffsetY,
            LyricFade = state.LyricFade, BackgroundDim = state.BackgroundDim,
            BackgroundSaturation = state.BackgroundSaturation, BackgroundBlur = state.BackgroundBlur,
            FlowSpeed = state.FlowSpeed, RenderScale = state.RenderScale, Preset = state.Preset, Fps = state.Fps,
            StageWidth = state.Preset == StagePresets.CustomId ? state.CustomStageWidth : null,
            StageHeight = state.Preset == StagePresets.CustomId ? state.CustomStageHeight : null,
            Tail = state.Tail, Head = state.Head, Encoder = encoder, EncoderPreset = encoderPreset, Quality = state.Quality,
            AudioCodec = state.AudioCodec, AudioBitrate = state.AudioBitrate, BrowserGpu = state.BrowserGpu,
        };
    }

    private void OnOutputTextChanged(object sender, TextChangedEventArgs e) => _overwriteConfirmedKey = null;

    /// <summary>覆盖确认绑定 canonical 输出路径与影响输出的关键配置；任一变化即要求重新确认。</summary>
    private static string BuildOverwriteKey(string canonicalOutput, AppState state) =>
        string.Join("|", canonicalOutput, state.AudioCodec, state.Head, state.Tail,
            StagePresets.Resolve(state.Preset).Id, state.CustomStageWidth, state.CustomStageHeight,
            state.Fps, state.AudioPath ?? "");

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _runner?.Cancel();
        SetStatus("Export.Canceling", "Export.StageCancel");
    }

    /// <summary>ffmpeg 以 -y 无条件覆盖输出；输入/输出同路径会同时读写损坏源文件，必须在启动前拦截。</summary>
    private static string? GetOutputConflict(string outputPath, AppState state)
    {
        if (!Path.IsPathFullyQualified(outputPath)) return LocalizationService.T("Export.OutputMustBeAbsolute");
        string fullOutput = Path.TrimEndingDirectorySeparator(outputPath);
        foreach ((string? path, string labelKey) in new[]
        {
            (state.AudioPath, "Export.AssetAudio"), (state.LyricPath, "Export.AssetLyric"), (state.CoverPath, "Export.AssetCover"),
        })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); } catch { continue; }
            if (string.Equals(fullPath, fullOutput, StringComparison.OrdinalIgnoreCase))
                return LocalizationService.T("Export.OutputSameAsAsset", LocalizationService.T(labelKey), fullPath);
        }
        return null;
    }

    private void OnRenderProgress(object? sender, RenderProgressEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        switch (args.Stage)
        {
            case "server":
                SetStatus("Export.LoadingPage", "Export.StageServer");
                Progress.Value = 1; break;
            case "ready":
                SetStatus("Export.AssetReady", "Export.StagePrepare");
                Progress.Value = 2;
                ProgressDetailText.Text = LocalizationService.T("Export.ReadyDetail", args.TotalMs / 1000.0, args.LyricLines); break;
            case "capture":
                SetStatus("Export.Capturing", "Export.StageCapture");
                Progress.Value = Math.Clamp(args.Percent, 0, 99);
                ProgressDetailText.Text = LocalizationService.T("Export.CaptureDetail", args.Frames, args.ElapsedMs / 1000.0, args.TotalMs / 1000.0); break;
            case "encode":
                SetStatus("Export.EncodingStage", "Export.StageEncode");
                Progress.Value = 99; break;
            case "done":
                Progress.Value = 100; _lastOutputPath = args.OutputPath;
                SetStatus("Export.RenderDone", "Export.StageDone");
                ProgressDetailText.Text = LocalizationService.T("Export.DoneDetail", args.OutputPath, args.Bytes / 1024.0 / 1024.0, args.Frames);
                OpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(args.OutputPath);
                PlayResultButton.IsEnabled = !string.IsNullOrWhiteSpace(args.OutputPath);
                ShowInfo(LocalizationService.T("Export.Success"), InfoBarSeverity.Success); break;
            case "error":
                SetStatus("Export.RenderFailed", "Export.StageError");
                AppendLog(LocalizationService.T("Export.LogError", args.Error));
                ShowInfo(args.Error ?? LocalizationService.T("Export.RenderFailedGeneric"), InfoBarSeverity.Error); break;
        }
    });

    private void OnRenderLog(object? sender, string line) => DispatcherQueue.TryEnqueue(() => AppendLog(line));

    private void OnRenderCompleted(object? sender, bool success) => DispatcherQueue.TryEnqueue(() =>
    {
        StartButton.IsEnabled = true; CancelButton.IsEnabled = false;
        if (!success && Progress.Value < 100)
        {
            SetStatus("Export.RenderFailed", "Export.StageEnded");
            ShowInfo(LocalizationService.T("Export.NotCompleted"), InfoBarSeverity.Error);
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

    private string _lastStatusKey = "Export.Ready";
    private string _lastStageKey = "Export.WaitingStart";

    /// <summary>状态文本按键存储：语言切换时 ApplyLocalization 可用当前键重新渲染。</summary>
    private void SetStatus(string statusKey, string stageKey)
    {
        _lastStatusKey = statusKey; _lastStageKey = stageKey;
        StatusText.Text = LocalizationService.T(statusKey);
        StageText.Text = $"{LocalizationService.T("Export.StagePrefix")}{LocalizationService.T(stageKey)}";
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        TaskInfoBar.Message = message; TaskInfoBar.Severity = severity; TaskInfoBar.IsOpen = true;
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath) && !ShellReveal.RevealInExplorer(_lastOutputPath))
            ShowInfo(LocalizationService.T("Export.LocateFailed"), InfoBarSeverity.Warning);
    }

    private async void OnPlayResult(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputPath) && !await ShellReveal.OpenMediaFileAsync(_lastOutputPath))
            ShowInfo(LocalizationService.T("Export.PlayFailed"), InfoBarSeverity.Warning);
    }

    private async Task<string?> ApplyPickedAssetAsync(Func<string?> getCurrentPath, Action<string> setPath, IReadOnlyCollection<string> extensions, PickerLocationId location)
    {
        string? path = await PickFileAsync(extensions, location);
        if (path is null || _state is null) return null;
        if (string.Equals(getCurrentPath(), path, StringComparison.OrdinalIgnoreCase)) _state.NotifyAssetReselected(path);
        else setPath(path);
        return path;
    }

    private async void OnPickAudio(object sender, RoutedEventArgs e)
    {
        string? path = await ApplyPickedAssetAsync(() => _state?.AudioPath, value => _state!.AudioPath = value, AudioExtensions, PickerLocationId.MusicLibrary);
        // 取消选择（path 为 null）不应触发标题填充。
        if (path is not null) AutoFillTitleFromAudio();
    }

    /// <summary>选中音频后默认用文件名填充标题：仅在标题为空，或仍等于上一次自动填充值时生效，绝不覆盖手输内容。</summary>
    private void AutoFillTitleFromAudio()
    {
        if (_state?.AudioPath is not { } path) return;
        string stem = Path.GetFileNameWithoutExtension(path).Trim();
        if (stem.Length == 0) return;
        string current = TitleBox.Text.Trim();
        if (current.Length == 0 || string.Equals(current, _autoFilledTitle, StringComparison.Ordinal))
        {
            _autoFilledTitle = stem;
            TitleBox.Text = stem;
        }
    }

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
