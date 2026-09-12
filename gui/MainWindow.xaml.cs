using AmllMvStudio.Services;
using AmllMvStudio.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AmllMvStudio;

public sealed partial class MainWindow : Window
{
    internal const double DesiredWidthDip = 1440;
    internal const double DesiredHeightDip = 940;
    internal const int WorkAreaMarginPixels = 80;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private readonly AppState _state = new();
    private readonly LocalAssetServer _server;

    public MainWindow()
    {
        InitializeComponent();

        Title = "AMLL Lyric MV Studio";

        // AppWindow 使用物理像素。将期望 DIP 按当前窗口 DPI 换算，再按工作区留边限制。
        if (AppWindow is not null)
        {
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea.WorkArea;
            uint dpi = GetDpiForWindow(windowHandle);
            (int width, int height) = GuiLayoutMetrics.CalculateInitialWindowSize(
                DesiredWidthDip, DesiredHeightDip, dpi, workArea.Width, workArea.Height, WorkAreaMarginPixels);
            AppWindow.Resize(new SizeInt32(width, height));

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }

        string projectRoot = RuntimePaths.ResolveContentRoot();
        _state.ProjectRoot = projectRoot;

        // 构建产物必须优先作为静态站根；源码 index.html 依赖 Vite 转换，直接提供会导致空白页。
        string webRoot = PreviewScripts.ResolveWebRoot(projectRoot);
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new DirectoryNotFoundException($"找不到预览页面：{Path.Combine(projectRoot, "dist", "index.html")}");
        }

        _server = new LocalAssetServer(webRoot);
        _server.Start();

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        PreviewView.WindowHandle = hwnd;
        ExportView.WindowHandle = hwnd;

        ApplyCommandLineArgs();

        PreviewView.Initialize(_state, _server);
        ExportView.Initialize(_state);

        Closed += (_, _) =>
        {
            // 先同步杀渲染进程树再关服务器：窗口销毁期 Unloaded 不保证在进程退出前执行完
            ExportView.Shutdown();
            _server.Dispose();
        };
    }

    /// <summary>
    /// 支持从命令行/资源管理器直接带素材启动：
    ///   AmllMvStudio.exe --audio "歌曲.flac" --lyric "歌词.ttml" --cover "封面.jpg" --preset 4k
    /// </summary>
    private void ApplyCommandLineArgs()
    {
        string[] argv = Environment.GetCommandLineArgs();
        for (int i = 1; i < argv.Length; i++)
        {
            string key = argv[i];
            bool hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
            string? value = hasValue ? argv[i + 1] : null;

            switch (key)
            {
                case "--audio" when value is not null:
                    if (File.Exists(value)) _state.AudioPath = Path.GetFullPath(value);
                    i++;
                    break;
                case "--lyric" when value is not null:
                    if (File.Exists(value)) _state.LyricPath = Path.GetFullPath(value);
                    i++;
                    break;
                case "--cover" when value is not null:
                    if (File.Exists(value)) _state.CoverPath = Path.GetFullPath(value);
                    i++;
                    break;
                case "--preset" when value is not null:
                    _state.Preset = value;
                    i++;
                    break;
                case "--fps" when value is not null:
                    if (int.TryParse(value, out int fps)) _state.Fps = Math.Clamp(fps, 24, 60);
                    i++;
                    break;
            }
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // NavigationView 在 XAML 解析期就会触发一次选中回调，
        // 那时内容区控件尚未创建完毕，需要跳过。
        if (PreviewView is null || ExportView is null || AboutView is null) return;
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag as string ?? "preview";

        bool preview = tag == "preview";
        PreviewView.SetActive(preview);
        PreviewView.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        ExportView.Visibility = tag == "export" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }
}
