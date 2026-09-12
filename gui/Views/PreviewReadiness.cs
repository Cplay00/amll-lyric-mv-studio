using System.Text.Json;

namespace AmllMvStudio.Views;

internal static class PreviewReadiness
{
    internal static bool IsReady(JsonElement diagnostic) =>
        GetBoolean(diagnostic, "rendered") &&
        GetBoolean(diagnostic, "bridge") &&
        GetBoolean(diagnostic, "setOptions") &&
        GetBoolean(diagnostic, "ready");

    internal static string? GetError(JsonElement diagnostic) =>
        diagnostic.TryGetProperty("error", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static string Describe(JsonElement diagnostic)
    {
        if (!diagnostic.TryGetProperty("readiness", out JsonElement readiness) ||
            readiness.ValueKind != JsonValueKind.Object) return "前端未提供就绪明细。";
        string[] names = ["assetsLoaded", "audioMetaLoaded", "fontLoaded", "stageLayoutReady", "backgroundReady"];
        string[] pending = names.Where(name => !GetBoolean(readiness, name)).ToArray();
        return pending.Length == 0 ? "桥接尚未报告 ready。" : $"等待 {string.Join(", ", pending)}。";
    }

    internal static TimeSpan GetRetryDelay(int completedAttempt) =>
        TimeSpan.FromMilliseconds(completedAttempt < 4 ? 100 : 500);

    private static bool GetBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
