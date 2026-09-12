namespace AmllMvStudio.Services;

/// <summary>与 src/lib/types.ts 中的 STAGE_PRESETS 保持一致（另含 GUI 专属的 custom 自定义像素尺寸）。</summary>
public sealed record StagePreset(string Id, string Label, int Width, int Height)
{
    public string Dimensions => $"{Width} × {Height} px";
    public string DisplayLabel => $"{Label} · {Dimensions}";
}

public static class StagePresets
{
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<StagePreset> All =
    [
        new("1080p", "1080P 横屏 16:9", 1920, 1080),
        new("1440p", "1440P 横屏 16:9", 2560, 1440),
        new("4k", "4K 横屏 16:9", 3840, 2160),
        new("vertical", "竖屏 9:16（短视频）", 1080, 1920),
        new("square", "方形 1:1", 1080, 1080),
        new(CustomId, "自定义像素尺寸", 1920, 1080),
    ];

    public static StagePreset Resolve(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>解析预设；custom 使用用户输入的像素尺寸（调用方保证已按 AppState 规范化）。</summary>
    public static StagePreset Resolve(string? id, int customWidth, int customHeight) =>
        id is not null && string.Equals(id, CustomId, StringComparison.OrdinalIgnoreCase)
            ? Resolve(CustomId) with { Width = customWidth, Height = customHeight }
            : Resolve(id);

    /// <summary>给定预设与实际生效尺寸，返回用于导出文件名/预览 query 的像素值。</summary>
    public static (int Width, int Height) EffectiveDimensions(string presetId, int customWidth, int customHeight) =>
        string.Equals(presetId, CustomId, StringComparison.OrdinalIgnoreCase)
            ? (customWidth, customHeight)
            : (Resolve(presetId).Width, Resolve(presetId).Height);
}
