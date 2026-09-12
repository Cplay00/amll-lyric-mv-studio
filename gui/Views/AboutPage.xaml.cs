using AmllMvStudio.Services;
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

    private bool _syncingLanguage = true;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = string.Format(LocalizationService.T("About.Version"), AboutContracts.GetDisplayVersion());
    }

    /// <summary>MainWindow 构造期调用一次：填充语言下拉并应用当前语言的静态文案。</summary>
    public void Initialize()
    {
        if (LanguageCombo.ItemsSource is null)
        {
            LanguageCombo.ItemsSource = LocalizationService.LanguageOptions;
            LanguageCombo.DisplayMemberPath = nameof(KeyValuePair<string, string>.Value);
        }
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        _syncingLanguage = true;
        string selected = LocalizationService.SelectedLanguage;
        foreach (object item in LanguageCombo.Items)
        {
            if (item is KeyValuePair<string, string> pair && pair.Key == selected)
                LanguageCombo.SelectedItem = item;
        }
        _syncingLanguage = false;

        PageTitle.Text = LocalizationService.T("About.Title");
        PageSubtitle.Text = LocalizationService.T("About.Subtitle");
        LanguageCombo.Header = LocalizationService.T("About.LanguageHeader");
        ToolTipService.SetToolTip(LanguageCombo, LocalizationService.T("About.LanguageTooltip"));
        LanguageHint.Text = LocalizationService.T("About.LanguageTooltip");
        VersionText.Text = string.Format(LocalizationService.T("About.Version"), AboutContracts.GetDisplayVersion());
        AuthorText.Text = LocalizationService.T("About.Author");
        DescriptionText.Text = LocalizationService.T("About.Description");
        LinksTitle.Text = LocalizationService.T("About.Links");
        ToolTipService.SetToolTip(GitHubLink, LocalizationService.T("About.GitHubTooltip"));
        UpstreamText.Text = LocalizationService.T("About.Upstream");
        ToolTipService.SetToolTip(UpstreamLink, LocalizationService.T("About.UpstreamTooltip"));
        LicenseTitle.Text = LocalizationService.T("About.License");
        LicenseText.Text = LocalizationService.T("About.LicenseText");
        EditionText.Text = LocalizationService.T("About.EditionText");
        LegalTitle.Text = LocalizationService.T("About.Legal");
        LegalNote.Text = LocalizationService.T("About.LegalNote");
        OpenLicenseText.Text = LocalizationService.T("About.OpenLicense");
        OpenThirdPartyText.Text = LocalizationService.T("About.OpenThirdParty");
        OpenNoticeText.Text = LocalizationService.T("About.OpenNotice");
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLanguage) return;
        if (LanguageCombo.SelectedItem is KeyValuePair<string, string> pair)
            LocalizationService.SetLanguage(pair.Key);
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
        if (!opened) ShowOpenError(LocalizationService.T("About.LinkError"));
    }

    private async Task OpenLegalFileAsync(string key)
    {
        string? path = ResolveLegalFilePath(key, AppContext.BaseDirectory);
        if (path is null || !File.Exists(path))
        {
            ShowOpenError(LocalizationService.T("About.FileMissing"));
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
        if (!opened) ShowOpenError(LocalizationService.T("About.FileOpenError"));
    }

    private void ShowOpenError(string message)
    {
        OpenInfoBar.Message = message;
        OpenInfoBar.Severity = InfoBarSeverity.Warning;
        OpenInfoBar.IsOpen = true;
    }
}
