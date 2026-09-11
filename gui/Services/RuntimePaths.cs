namespace AmllMvStudio.Services;

/// <summary>统一开发目录与发布目录的内容、Node、FFmpeg 和浏览器布局。</summary>
public static class RuntimePaths
{
    public static string ResolveContentRoot(string? baseDirectory = null)
    {
        string baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        foreach (string candidate in AdjacentCandidates(baseDir, "runtime", "content")
                     .Concat([Path.Combine(baseDir, "content"), baseDir]))
        {
            if (HasRuntimeContent(candidate)) return Path.GetFullPath(candidate);
        }

        string? directory = baseDir;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
        {
            if (HasRuntimeContent(directory)) return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        return Path.Combine(baseDir, "runtime", "content");
    }

    public static string? FindNodeExecutable(string? baseDirectory = null)
    {
        string baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var candidates = new List<string>();
        candidates.AddRange(AdjacentCandidates(baseDir, "runtime", "node", "node.exe"));
        candidates.AddRange(AdjacentCandidates(baseDir, "runtime", "node.exe"));
        candidates.AddRange(AdjacentCandidates(baseDir, "node", "node.exe"));
        candidates.AddRange(AdjacentCandidates(baseDir, "node.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"));
        return FirstFile(candidates) ?? FindOnPath("node.exe");
    }

    public static string? FindFfmpegExecutable(string projectRoot, string? baseDirectory = null)
    {
        string baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var preferredCandidates = new List<string>();
        preferredCandidates.AddRange(AdjacentCandidates(baseDir, "runtime", "ffmpeg", "ffmpeg.exe"));
        preferredCandidates.Add(Path.Combine(projectRoot, "installer", "staging", "runtime", "ffmpeg", "ffmpeg.exe"));
        string[] legacyFallbacks =
        [
            Path.Combine(projectRoot, "node_modules", "@ffmpeg-installer", "win32-x64", "ffmpeg.exe"),
            Path.Combine(projectRoot, "node_modules", "@ffmpeg-installer", "ffmpeg", "ffmpeg.exe"),
        ];
        return FirstFile(preferredCandidates.Concat(legacyFallbacks)) ?? FindOnPath("ffmpeg.exe");
    }

    public static string? FindBrowserExecutable(string? baseDirectory = null)
    {
        string baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        candidates.AddRange(FindBundledBrowsers(baseDir));
        candidates.AddRange(AdjacentCandidates(baseDir, "runtime", "browser", "msedge.exe"));
        candidates.AddRange(AdjacentCandidates(baseDir, "runtime", "browser", "chrome.exe"));
        candidates.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
        candidates.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"));
        return FirstFile(candidates);
    }

    public static bool HasRuntimeContent(string directory) =>
        File.Exists(Path.Combine(directory, "scripts", "render-mv.mjs")) &&
        (File.Exists(Path.Combine(directory, "index.html")) ||
         File.Exists(Path.Combine(directory, "dist", "index.html")));

    private static IEnumerable<string> FindBundledBrowsers(string baseDir)
    {
        string? directory = baseDir;
        for (int depth = 0; depth < 3 && directory is not null; depth++)
        {
            string root = Path.Combine(directory, "runtime", "browsers");
            if (Directory.Exists(root))
            {
                IEnumerable<string> chrome = Directory.EnumerateFiles(root, "chrome.exe", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
                IEnumerable<string> edge = Directory.EnumerateFiles(root, "msedge.exe", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
                foreach (string path in chrome.Concat(edge)) yield return path;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
    }

    private static IEnumerable<string> AdjacentCandidates(string baseDir, params string[] parts)
    {
        string? directory = baseDir;
        for (int depth = 0; depth < 3 && directory is not null; depth++)
        {
            yield return Path.Combine([directory, .. parts]);
            directory = Directory.GetParent(directory)?.FullName;
        }
    }

    private static string? FirstFile(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(File.Exists) is { } path ? Path.GetFullPath(path) : null;

    private static string? FindOnPath(string fileName)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
