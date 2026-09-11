using Microsoft.UI.Xaml;

namespace AmllMvStudio;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // WinUI 的 XAML 加载错误默认只会让进程静默退出，
        // 这里统一落盘，便于排查（日志位置：%LOCALAPPDATA%\AmllMvStudio\startup-error.log）
        UnhandledException += (_, e) =>
        {
            WriteCrashLog("UnhandledException", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnLaunched", ex);
            throw;
        }
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmllMvStudio");
            Directory.CreateDirectory(dir);

            string text =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{new string('-', 70)}{Environment.NewLine}";

            File.AppendAllText(Path.Combine(dir, "startup-error.log"), text);
        }
        catch
        {
            // 日志写入失败时无能为力，避免二次异常
        }
    }
}
