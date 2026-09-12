using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AmllMvStudio.Services;

/// <summary>预览、导出与命令行渲染共享的完整工作台配置。</summary>
public sealed class AppState : INotifyPropertyChanged
{
    private string? _audioPath;
    private string? _lyricPath;
    private string? _coverPath;
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _fontFamily = "Microsoft YaHei UI";
    private double _fontScale = 1;
    private int _fontWeight = 600;
    private string _secondaryMode = RenderContracts.DefaultSecondaryMode;
    private bool _showSongInfo = true;
    private bool _showArtist = true;
    private bool _showAlbum = true;
    private bool _showArtwork = true;
    private bool _showControls;
    private double _artworkScale = 1;
    private string _lyricAlign = "left";
    private double _lyricOffset;
    private double _lyricWidth = RenderContracts.DefaultLyricWidth;
    private double _lyricHeight = RenderContracts.MaxLyricHeight;
    private double _infoOffsetX;
    private double _infoOffsetY;
    private int _customStageWidth = 1920;
    private int _customStageHeight = 1080;
    private double _lyricFade = RenderContracts.DefaultLyricFade;
    private double _backgroundDim = 0.35;
    private double _backgroundSaturation = RenderContracts.DefaultBackgroundSaturation;
    private double _backgroundBlur = 24;
    private double _flowSpeed = 2;
    private double _renderScale = 1;
    private string _preset = "1080p";
    private int _fps = 60;
    private int _head;
    private int _tail = 1200;
    private string _encoder = RenderContracts.DefaultEncoder;
    private string _encoderPreset = "";
    private int _quality = 16;
    private string _audioCodec = RenderContracts.DefaultAudioCodec;
    private string _audioBitrate = "320k";
    private string _browserGpu = RenderContracts.DefaultBrowserGpu;
    private string _projectRoot = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AssetsChanged;

    public string? AudioPath { get => _audioPath; set => SetAsset(ref _audioPath, value); }
    public string? LyricPath { get => _lyricPath; set => SetAsset(ref _lyricPath, value); }
    public string? CoverPath { get => _coverPath; set => SetAsset(ref _coverPath, value); }

    public string Title { get => _title; set => Set(ref _title, value ?? ""); }
    public string Artist { get => _artist; set => Set(ref _artist, value ?? ""); }
    public string Album { get => _album; set => Set(ref _album, value ?? ""); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value ?? ""); }
    public double FontScale { get => _fontScale; set => Set(ref _fontScale, RenderContracts.ClampFinite(value, 0.5, 2, 1)); }
    public int FontWeight { get => _fontWeight; set => Set(ref _fontWeight, Math.Clamp(value, 100, 900)); }
    public string SecondaryMode { get => _secondaryMode; set => Set(ref _secondaryMode, RenderContracts.NormalizeSecondaryMode(value)); }
    public bool ShowSongInfo { get => _showSongInfo; set => Set(ref _showSongInfo, value); }
    public bool ShowArtist { get => _showArtist; set => Set(ref _showArtist, value); }
    public bool ShowAlbum { get => _showAlbum; set => Set(ref _showAlbum, value); }
    public bool ShowArtwork { get => _showArtwork; set => Set(ref _showArtwork, value); }
    public bool ShowControls { get => _showControls; set => Set(ref _showControls, value); }
    public double ArtworkScale { get => _artworkScale; set => Set(ref _artworkScale, RenderContracts.ClampFinite(value, 0.5, 1.5, 1)); }
    public string LyricAlign { get => _lyricAlign; set => Set(ref _lyricAlign, value is "center" or "right" ? value : "left"); }
    public double LyricOffset { get => _lyricOffset; set => Set(ref _lyricOffset, RenderContracts.ClampFinite(value, -600, 600, 0)); }
    public double LyricWidth { get => _lyricWidth; set => Set(ref _lyricWidth, RenderContracts.ClampFinite(value, RenderContracts.MinLyricWidth, RenderContracts.MaxLyricWidth, RenderContracts.DefaultLyricWidth)); }
    public double LyricHeight { get => _lyricHeight; set => Set(ref _lyricHeight, RenderContracts.ClampFinite(value, RenderContracts.MinLyricHeight, RenderContracts.MaxLyricHeight, RenderContracts.MaxLyricHeight)); }
    public double InfoOffsetX { get => _infoOffsetX; set => Set(ref _infoOffsetX, RenderContracts.ClampFinite(value, RenderContracts.MinInfoOffset, RenderContracts.MaxInfoOffset, 0)); }
    public double InfoOffsetY { get => _infoOffsetY; set => Set(ref _infoOffsetY, RenderContracts.ClampFinite(value, RenderContracts.MinInfoOffset, RenderContracts.MaxInfoOffset, 0)); }
    public double LyricFade { get => _lyricFade; set => Set(ref _lyricFade, RenderContracts.ClampFinite(value, 0, 0.3, RenderContracts.DefaultLyricFade)); }
    public double BackgroundDim { get => _backgroundDim; set => Set(ref _backgroundDim, RenderContracts.ClampFinite(value, 0, 0.9, 0.35)); }
    public double BackgroundSaturation { get => _backgroundSaturation; set => Set(ref _backgroundSaturation, RenderContracts.ClampFinite(value, 0, 2, RenderContracts.DefaultBackgroundSaturation)); }
    public double BackgroundBlur { get => _backgroundBlur; set => Set(ref _backgroundBlur, RenderContracts.ClampFinite(value, 0, 80, 24)); }
    public double FlowSpeed { get => _flowSpeed; set => Set(ref _flowSpeed, RenderContracts.ClampFinite(value, 0, 5, 2)); }
    public double RenderScale { get => _renderScale; set => Set(ref _renderScale, RenderContracts.ClampFinite(value, 0.25, 1, 1)); }
    public string Preset { get => _preset; set => Set(ref _preset, StagePresets.Resolve(value).Id); }
    public int Fps { get => _fps; set => Set(ref _fps, Math.Clamp(value, 24, 120)); }
    public int CustomStageWidth { get => _customStageWidth; set => Set(ref _customStageWidth, NormalizeStageSize(value, 1920)); }
    public int CustomStageHeight { get => _customStageHeight; set => Set(ref _customStageHeight, NormalizeStageSize(value, 1080)); }
    public int Head { get => _head; set => Set(ref _head, Math.Clamp(value, 0, RenderContracts.MaxTimingPaddingMs)); }
    public int Tail { get => _tail; set { if (Set(ref _tail, Math.Clamp(value, 0, RenderContracts.MaxTimingPaddingMs))) OnPropertyChanged(nameof(TailMs)); } }
    public string Encoder { get => _encoder; set => Set(ref _encoder, RenderContracts.NormalizeEncoder(value)); }
    public string EncoderPreset { get => _encoderPreset; set => Set(ref _encoderPreset, value ?? ""); }
    public int Quality { get => _quality; set { if (Set(ref _quality, value)) OnPropertyChanged(nameof(Crf)); } }
    public string AudioCodec { get => _audioCodec; set => Set(ref _audioCodec, RenderContracts.NormalizeAudioCodec(value)); }
    public string AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value ?? "320k"); }
    public string BrowserGpu { get => _browserGpu; set => Set(ref _browserGpu, RenderContracts.NormalizeBrowserGpu(value)); }
    public string ProjectRoot { get => _projectRoot; set => Set(ref _projectRoot, value ?? ""); }

    // 兼容旧 GUI 调用；新代码统一使用 Quality 与 Tail。
    public int Crf { get => Quality; set => Quality = value; }
    public int TailMs { get => Tail; set => Tail = value; }
    public bool HasAudio => !string.IsNullOrWhiteSpace(_audioPath);

    public static bool IsTitleValid(string? title) => !string.IsNullOrWhiteSpace(title);

    public void NotifyAssetReselected(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string? name = string.Equals(path, _audioPath, StringComparison.OrdinalIgnoreCase) ? nameof(AudioPath)
            : string.Equals(path, _lyricPath, StringComparison.OrdinalIgnoreCase) ? nameof(LyricPath)
            : string.Equals(path, _coverPath, StringComparison.OrdinalIgnoreCase) ? nameof(CoverPath) : null;
        if (name is null) return;
        OnPropertyChanged(name);
        AssetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int NormalizeStageSize(int value, int fallback)
    {
        if (value < RenderContracts.MinStageSize || value > RenderContracts.MaxStageSize) return fallback;
        // H.264/HEVC yuv420p 要求偶数尺寸。
        return value - (value % 2);
    }

    private void SetAsset(ref string? field, string? value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(AudioPath)) OnPropertyChanged(nameof(HasAudio));
        AssetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
