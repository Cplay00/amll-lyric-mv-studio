using System.Collections.ObjectModel;

namespace AmllMvStudio.Services;

/// <summary>GUI、预览 query 与 Node 渲染器共享的规范值域。</summary>
public static class RenderContracts
{
    public const string DefaultSecondaryMode = "both";
    public const double DefaultLyricWidth = 54;
    public const double DefaultLyricFade = 0.08;
    public const string DefaultBrowserGpu = "auto";
    public const string DefaultAudioCodec = "aac";
    public const string DefaultEncoder = "auto";
    public const double DefaultBackgroundSaturation = 1.2;
    public const int MaxTimingPaddingMs = 600_000;
    public const string AudioCopyOffsetWarning = "音频 copy 仅支持 MKV 输出，且片头与片尾留白必须均为 0 ms；若音频与首帧存在时间偏移，渲染仍可能失败，建议使用 AAC。";

    public const double MinLyricWidth = 30;
    public const double MaxLyricWidth = 100;
    public const double MinLyricFade = 0;
    public const double MaxLyricFade = 0.3;
    public const double MinLyricOffset = -600;
    public const double MaxLyricOffset = 600;
    public const double MinBackgroundSaturation = 0;
    public const double MaxBackgroundSaturation = 2;
    public const double MinInfoOffset = -30;
    public const double MaxInfoOffset = 30;
    public const double MinLyricHeight = 20;
    public const double MaxLyricHeight = 100;
    public const int MinStageSize = 256;
    public const int MaxStageSize = 8192;

    public static readonly IReadOnlyList<string> SecondaryModes = ReadOnly("none", "translation", "roman", "both");
    public static readonly IReadOnlyList<string> BrowserGpuModes = ReadOnly("auto", "on", "off");
    public static readonly IReadOnlyList<string> AudioCodecs = ReadOnly("aac", "copy", "flac");
    public static readonly IReadOnlyList<string> Encoders = ReadOnly(
        "auto", "libx264", "libx265", "h264_nvenc", "hevc_nvenc",
        "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf");

    private static readonly IReadOnlyList<string> SoftwarePresets = ReadOnly("ultrafast", "fast", "medium", "slow", "veryslow");
    private static readonly IReadOnlyList<string> NvencPresets = ReadOnly("p1", "p2", "p3", "p4", "p5", "p6", "p7");
    private static readonly IReadOnlyList<string> LegacyNvencPresets = ReadOnly("fast", "medium", "slow");
    private static readonly IReadOnlyList<string> QsvPresets = ReadOnly("veryfast", "medium", "veryslow");
    private static readonly IReadOnlyList<string> AmfPresets = ReadOnly("speed", "balanced", "quality");
    private static readonly IReadOnlyList<string> NoPresets = Array.Empty<string>();

    public static IReadOnlyList<string> GetEncoderPresets(string encoder, bool modernNvenc = true)
    {
        if (encoder is "libx264" or "libx265") return SoftwarePresets;
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal)) return modernNvenc ? NvencPresets : LegacyNvencPresets;
        if (encoder.EndsWith("_qsv", StringComparison.Ordinal)) return QsvPresets;
        if (encoder.EndsWith("_amf", StringComparison.Ordinal)) return AmfPresets;
        return NoPresets;
    }

    public static IReadOnlyList<string> GetSupportedEncoderPresets(
        string encoder,
        IEnumerable<string>? probedPresets) =>
        encoder == "auto" || probedPresets is null
            ? NoPresets
            : GetEncoderPresets(encoder, modernNvenc: true)
                .Concat(GetEncoderPresets(encoder, modernNvenc: false))
                .Distinct(StringComparer.Ordinal)
                .Where(new HashSet<string>(probedPresets, StringComparer.Ordinal).Contains)
                .ToArray();

    public static string GetDefaultEncoderPreset(string encoder, bool modernNvenc = true)
    {
        IReadOnlyList<string> presets = GetEncoderPresets(encoder, modernNvenc);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal)) return modernNvenc ? "p4" : "medium";
        if (encoder.EndsWith("_amf", StringComparison.Ordinal)) return "balanced";
        return presets.Count == 0 ? "" : "medium";
    }

    public static bool IsKnownEncoderPreset(string encoder, string preset)
    {
        if (encoder == "auto") return string.IsNullOrEmpty(preset);
        return GetEncoderPresets(encoder).Contains(preset, StringComparer.Ordinal) ||
               (encoder.EndsWith("_nvenc", StringComparison.Ordinal) &&
                GetEncoderPresets(encoder, modernNvenc: false).Contains(preset, StringComparer.Ordinal));
    }

    public static string NormalizeSecondaryMode(string? value) =>
        NormalizeChoice(value, SecondaryModes, DefaultSecondaryMode);

    public static string NormalizeBrowserGpu(string? value) =>
        NormalizeChoice(value, BrowserGpuModes, DefaultBrowserGpu);

    public static string NormalizeAudioCodec(string? value) =>
        NormalizeChoice(value, AudioCodecs, DefaultAudioCodec);

    public static string NormalizeEncoder(string? value) =>
        NormalizeChoice(value, Encoders, DefaultEncoder);

    public static string GetOutputExtension(string? outputPath) =>
        string.IsNullOrWhiteSpace(outputPath) ? ".mp4" : Path.GetExtension(outputPath).ToLowerInvariant();

    public static string? GetAudioOutputError(string? outputPath, string audioCodec, int headMs, int tailMs)
    {
        string extension = GetOutputExtension(outputPath);
        if (headMs is < 0 or > MaxTimingPaddingMs || tailMs is < 0 or > MaxTimingPaddingMs)
            return $"片头与片尾时长必须介于 0 和 {MaxTimingPaddingMs} 毫秒之间。";
        // 与 render-lib.validateContainer 同源：非法容器在 GUI 就拦截，不要等抓帧完成后才失败
        if (extension is not (".mp4" or ".mkv" or ".mov"))
            return "输出容器仅支持 .mp4、.mov 或 .mkv；请修正输出文件名。";
        if (extension == ".mp4" && audioCodec == "flac")
            return "MP4 不支持 FLAC 音频；请选择 AAC/copy，或将输出文件改为 .mkv/.mov。";
        if (audioCodec == "copy" && extension != ".mkv")
            return "音频 copy 仅支持 MKV 输出；请将输出文件改为 .mkv，或使用 AAC/FLAC。";
        if (audioCodec == "copy" && (headMs > 0 || tailMs > 0))
            return "音频 copy 无法处理片头或片尾时序变换；请将两者设为 0 ms，或改用 AAC/FLAC。";
        return null;
    }

    public static bool TryResolveGuiOutputPath(
        string? outputText,
        string projectRoot,
        string audioPath,
        int width,
        int height,
        int fps,
        out string? outputPath,
        out string? error)
    {
        try
        {
            string candidate;
            if (string.IsNullOrWhiteSpace(outputText))
            {
                string audioStem = SanitizeFileName(Path.GetFileNameWithoutExtension(audioPath));
                candidate = Path.Combine(projectRoot, "out", $"{audioStem}-{width}x{height}-{fps}fps.mp4");
            }
            else
            {
                candidate = outputText.Trim();
                if (!Path.IsPathFullyQualified(candidate)) candidate = Path.Combine(projectRoot, candidate);
            }
            outputPath = Path.GetFullPath(candidate);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            outputPath = null;
            error = $"输出路径无效：{ex.Message}";
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        string sanitized = new(value.Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch).Take(120).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "mv" : sanitized;
    }

    public static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public static void RequireRange(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"{name} 必须是 {min}..{max} 的有限数值。");
    }

    public static void ValidateChoice(string value, IReadOnlyList<string> choices, string name)
    {
        if (!choices.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException($"{name} 必须是：{string.Join(" / ", choices)}。", name);
    }

    private static string NormalizeChoice(string? value, IReadOnlyList<string> choices, string fallback) =>
        value is not null && choices.Contains(value, StringComparer.Ordinal) ? value : fallback;

    private static ReadOnlyCollection<string> ReadOnly(params string[] values) => Array.AsReadOnly(values);
}
