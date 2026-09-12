using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace AmllMvStudio.Views;

public sealed partial class AboutPage : UserControl
{
    internal static readonly Uri GitHubUri = new(
        "https://github.com/Cplay00/amll-lyric-mv-studio", UriKind.Absolute);
    internal static readonly Uri UpstreamUri = new(
        "https://github.com/amll-dev/applemusic-like-lyrics", UriKind.Absolute);

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"产品版本 {AboutContracts.GetDisplayVersion()}";
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs e) =>
        await OpenTrustedUriAsync(GitHubUri);

    private async void OnUpstreamClick(object sender, RoutedEventArgs e) =>
        await OpenTrustedUriAsync(UpstreamUri);

    private async void OnLicenseClick(object sender, RoutedEventArgs e) =>
        await OpenLegalFileAsync("license");

    private async void OnThirdPartyClick(object sender, RoutedEventArgs e) =>
        await OpenLegalFileAsync("third-party");

    private async void OnLegalNoticeClick(object sender, RoutedEventArgs e) =>
        await OpenLegalFileAsync("notice");

    internal static bool IsTrustedProjectUri(Uri uri) => AboutContracts.IsTrustedProjectUri(uri);

    internal static string? ResolveLegalFilePath(string key, string baseDirectory) =>
        AboutContracts.ResolveLegalFilePath(key, baseDirectory);

    private async Task OpenTrustedUriAsync(Uri uri)
    {
        bool opened = false;
        try
        {
            if (IsTrustedProjectUri(uri)) opened = await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            opened = false;
        }
        if (!opened) ShowOpenError("无法安全打开项目链接。");
    }

    private async Task OpenLegalFileAsync(string key)
    {
        string? path = ResolveLegalFilePath(key, AppContext.BaseDirectory);
        if (path is null || !File.Exists(path))
        {
            ShowOpenError("此发行版本未包含所选法律文件。");
            return;
        }

        bool opened = false;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            opened = await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = false });
        }
        catch
        {
            opened = false;
        }
        if (!opened) ShowOpenError("无法使用系统默认文本查看器打开法律文件。");
    }

    private void ShowOpenError(string message)
    {
        OpenInfoBar.Message = message;
        OpenInfoBar.Severity = InfoBarSeverity.Warning;
        OpenInfoBar.IsOpen = true;
    }
}
