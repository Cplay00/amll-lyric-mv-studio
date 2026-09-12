using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace AmllMvStudio.Services;

/// <summary>
/// 在资源管理器中定位文件，以及用系统默认程序打开文件。
///
/// 刻意不拼接命令行、也不经 ShellExecute 启动用户提供的路径，原因有二：
///
/// 1. 旧写法 <c>Arguments = $"/select,\"{path}\""</c> 把路径拼进命令行走 ShellExecute。
///    实测该形式并不会被 cmd 二次解析（explorer.exe 是直接启动的可执行文件，
///    Arguments 不经 shell），且 Windows 文件名不允许双引号，无法闭合引号逃逸，
///    因此**不构成可利用的命令注入**。但字符串拼接命令行本身是高危模式，
///    一旦将来换成会经 shell 的宿主就会立刻变成真漏洞，故直接消除该模式。
///
/// 2. 更实际的问题是：<c>ShellExecute</c> 打开一个用户可自由输入的路径时，
///    会按文件关联执行它——若指向 .exe/.bat 就是直接运行。
///    输出路径来自界面上的自由文本框，因此这里改为限制可打开的扩展名。
///
/// 替代实现：
///   - SHOpenFolderAndSelectItems（Explorer 自身使用的 Shell API，直接接收 PIDL）
///   - Launcher.LaunchFileAsync（WinRT，交由系统按文件关联打开）
/// </summary>
public static class ShellReveal
{
    private const string TestPipeEnvironmentVariable = "AMLL_OUTPUT_ACTION_TEST_PIPE";
    private const string TestTokenEnvironmentVariable = "AMLL_OUTPUT_ACTION_TEST_TOKEN";
    private const string TestPipePrefix = "amll-output-action-";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>
    /// 打开文件所在目录并选中该文件。
    /// 路径作为 PIDL 传入 Shell API，不经过任何命令行解析，无需转义。
    /// </summary>
    public static bool RevealInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            // 路径非法（含非法字符等）时直接放弃
            return false;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return false;
        if (TryRecordTestLaunch("reveal", fullPath, out bool recorded)) return recorded;

        string? parentPath = Directory.Exists(fullPath)
            ? Directory.GetParent(fullPath)?.FullName
            : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentPath)) return false;

        int folderHr = SHParseDisplayName(parentPath, IntPtr.Zero, out IntPtr folderPidl, 0, out _);
        if (folderHr != 0 || folderPidl == IntPtr.Zero) return false;

        int itemHr = SHParseDisplayName(fullPath, IntPtr.Zero, out IntPtr itemPidl, 0, out _);
        if (itemHr != 0 || itemPidl == IntPtr.Zero)
        {
            CoTaskMemFree(folderPidl);
            return false;
        }

        try
        {
            IntPtr childPidl = ILFindLastID(itemPidl);
            return childPidl != IntPtr.Zero &&
                SHOpenFolderAndSelectItems(folderPidl, 1, [childPidl], 0) == 0;
        }
        finally
        {
            CoTaskMemFree(itemPidl);
            CoTaskMemFree(folderPidl);
        }
    }

    /// <summary>会用系统默认关联程序打开的扩展名白名单（渲染产物为 mp4/webm）</summary>
    private static readonly string[] LaunchableExtensions =
    [
        ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi",
    ];

    private static bool TryRecordTestLaunch(string action, string fullPath, out bool result)
    {
        result = false;
        string? pipeName = Environment.GetEnvironmentVariable(TestPipeEnvironmentVariable);
        string? token = Environment.GetEnvironmentVariable(TestTokenEnvironmentVariable);
        if (pipeName is null && token is null) return false;
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token) ||
            !pipeName.StartsWith(TestPipePrefix, StringComparison.Ordinal) ||
            pipeName.Length != TestPipePrefix.Length + 32 ||
            pipeName.Skip(TestPipePrefix.Length).Any(character => !Uri.IsHexDigit(character)) ||
            token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
            return true;

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect(5000);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: false);
            writer.Write(token);
            writer.Write(action);
            writer.Write(fullPath);
            result = true;
        }
        catch (Exception)
        {
            result = false;
        }
        return true;
    }

    /// <summary>
    /// 用系统默认程序打开渲染产物。
    /// 仅允许已知的媒体扩展名，避免把用户提供的路径当作可执行文件启动。
    /// </summary>
    public static async Task<bool> OpenMediaFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return false;
        }

        if (!File.Exists(fullPath)) return false;

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (Array.IndexOf(LaunchableExtensions, ext) < 0) return false;
        if (TryRecordTestLaunch("play", fullPath, out bool recorded)) return recorded;

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(fullPath);
            return await Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch (Exception)
        {
            // 无默认关联程序、或被系统策略拒绝时返回失败，交由调用方提示
            return false;
        }
    }
}
