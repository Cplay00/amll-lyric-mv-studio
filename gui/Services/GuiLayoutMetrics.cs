namespace AmllMvStudio.Services;

internal static class GuiLayoutMetrics
{
    internal const double StackLayoutBreakpoint = 980;
    internal const double CompactLayoutBreakpoint = 620;
    internal const double CategoryLayoutBreakpoint = 520;
    internal const double ParameterWidthRatio = 0.8;
    internal const double ParameterMaxWidth = 960;

    internal static bool ShouldStackMainLayout(double width) =>
        width < StackLayoutBreakpoint;

    internal static bool ShouldUseCompactLayout(double width) =>
        width < CompactLayoutBreakpoint;

    internal static double GetParameterPanelWidth(double width, bool stacked)
    {
        if (!double.IsFinite(width) || width <= 0) return 0;
        double availableWidth = stacked ? width : width * 3 / 5;
        return Math.Min(availableWidth * ParameterWidthRatio, ParameterMaxWidth);
    }

    internal static (int Width, int Height) CalculateInitialWindowSize(
        double desiredWidthDip,
        double desiredHeightDip,
        uint dpi,
        int workAreaWidthPixels,
        int workAreaHeightPixels,
        int marginPixels)
    {
        double scale = dpi > 0 ? dpi / 96d : 1d;
        int desiredWidthPixels = ToPhysicalPixels(desiredWidthDip, scale);
        int desiredHeightPixels = ToPhysicalPixels(desiredHeightDip, scale);
        int availableWidth = Math.Max(1, workAreaWidthPixels - Math.Max(0, marginPixels));
        int availableHeight = Math.Max(1, workAreaHeightPixels - Math.Max(0, marginPixels));
        return (
            Math.Min(desiredWidthPixels, availableWidth),
            Math.Min(desiredHeightPixels, availableHeight));
    }

    private static int ToPhysicalPixels(double dip, double scale)
    {
        if (!double.IsFinite(dip) || dip <= 0) return 1;
        double pixels = Math.Round(dip * scale, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(pixels, 1, int.MaxValue);
    }
}
