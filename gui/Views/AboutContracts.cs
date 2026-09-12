using System.Reflection;
using System.Security;

namespace AmllMvStudio.Views;

internal static class AboutContracts
{
    internal static string GetDisplayVersion()
    {
        Assembly assembly = typeof(AboutContracts).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return FormatDisplayVersion(informationalVersion, assembly.GetName().Version);
    }

    internal static string FormatDisplayVersion(string? informationalVersion, Version? assemblyVersion)
    {
        string candidate = informationalVersion?.Trim() ?? string.Empty;
        int metadataSeparator = candidate.IndexOf('+');
        if (metadataSeparator >= 0) candidate = candidate[..metadataSeparator];
        if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        return assemblyVersion?.ToString() ?? "未知";
    }

    internal static bool IsTrustedProjectUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        uri.AbsolutePath.Length > 1;

    internal static string? ResolveLegalFilePath(string? key, string? baseDirectory)
    {
        string? fileName = key switch
        {
            "license" => "LICENSE.txt",
            "third-party" => "THIRD-PARTY-NOTICES.md",
            "notice" => "NOTICE.txt",
            _ => null,
        };
        if (fileName is null || string.IsNullOrWhiteSpace(baseDirectory)) return null;

        try
        {
            string legalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            string candidate = Path.GetFullPath(Path.Combine(legalRoot, fileName));
            string? candidateDirectory = Path.GetDirectoryName(candidate);
            return string.Equals(candidateDirectory, legalRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal)
                ? candidate
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}
