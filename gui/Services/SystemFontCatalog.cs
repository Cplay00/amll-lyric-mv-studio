using System.Runtime.InteropServices;

namespace AmllMvStudio.Services;

/// <summary>使用 Windows GDI（DirectWrite 的系统字体来源）枚举已安装字体族。</summary>
public static class SystemFontCatalog
{
    private static readonly string[] FallbackFamilies =
    [
        "Microsoft YaHei UI", "Segoe UI", "Arial", "SimSun",
    ];

    public static IReadOnlyList<string> GetFontFamilies() => GetFontFamilies(EnumerateWindowsFonts);

    internal static IReadOnlyList<string> GetFontFamilies(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            string[] families = enumerate()
                .Select(name => name.Trim())
                .Where(name => name.Length > 0 && !name.StartsWith('@'))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return families.Length > 0 ? families : FallbackFamilies;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return FallbackFamilies;
        }
    }

    private static IEnumerable<string> EnumerateWindowsFonts()
    {
        nint hdc = GetDC(nint.Zero);
        if (hdc == nint.Zero) throw new ExternalException("无法获取字体枚举设备上下文。");
        try
        {
            var families = new List<string>();
            var filter = new LogFont { CharSet = DefaultCharacterSet, FaceName = string.Empty };
            EnumFontFamExProc callback = (in LogFont font, nint _, uint _, nint _) =>
            {
                if (!string.IsNullOrWhiteSpace(font.FaceName)) families.Add(font.FaceName);
                return 1;
            };
            if (EnumFontFamiliesEx(hdc, ref filter, callback, nint.Zero, 0) == 0 && families.Count == 0)
                throw new ExternalException("Windows 未返回可用字体族。");
            GC.KeepAlive(callback);
            return families;
        }
        finally
        {
            _ = ReleaseDC(nint.Zero, hdc);
        }
    }

    private const byte DefaultCharacterSet = 1;
    private const int FaceSize = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = FaceSize)]
        public string FaceName;
    }

    private delegate int EnumFontFamExProc(in LogFont font, nint textMetric, uint fontType, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(
        nint hdc, ref LogFont logFont, EnumFontFamExProc callback, nint parameter, uint flags);
}
