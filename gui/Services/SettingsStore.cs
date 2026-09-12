using System.Text.Json;

namespace AmllMvStudio.Services;

/// <summary>
/// 用户偏好持久化：%LOCALAPPDATA%\AmllMvStudio\settings.json。
/// 与 WebView2 配置目录同根；读写失败一律静默回退，不能阻断启动。
/// </summary>
public static class SettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmllMvStudio", "settings.json");

    public static string? ReadValue(string key)
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(key, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void WriteValue(string key, string value)
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Dictionary<string, string> settings = [];
            if (File.Exists(path))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            settings[property.Name] = property.Value.GetString() ?? "";
                    }
                }
                catch (JsonException) { /* 损坏文件视为空配置重建 */ }
            }
            settings[key] = value;
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 偏好写入失败可接受：运行期仍使用当前选择。
        }
    }
}
