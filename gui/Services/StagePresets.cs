namespace AmllMvStudio.Services;

/// <summary>与 src/lib/types.ts 中的 STAGE_PRESETS 保持一致</summary>
public sealed record StagePreset(string Id, string Label, int Width, int Height)
{
    public string Dimensions => $"{Width} × {Height} px";
    public string DisplayLabel => $"{Label} · {Dimensions}";
}

public static class StagePresets
{
    public static readonly IReadOnlyList<StagePreset> All =
    [
        new("1080p", "1080P 横屏 16:9", 1920, 1080),
        new("1440p", "1440P 横屏 16:9", 2560, 1440),
        new("4k", "4K 横屏 16:9", 3840, 2160),
        new("vertical", "竖屏 9:16（短视频）", 1080, 1920),
        new("square", "方形 1:1", 1080, 1080),
    ];

    public static StagePreset Resolve(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
