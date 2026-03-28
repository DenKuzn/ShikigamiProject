using System.Windows.Media;

namespace Shikigami.Runner.Theme;

/// <summary>
/// Deep Space color palette — matches the Python original.
/// </summary>
public static class DeepSpaceTheme
{
    public static readonly Color Bg        = (Color)ColorConverter.ConvertFromString("#0b0e17");
    public static readonly Color BgDark    = (Color)ColorConverter.ConvertFromString("#060a10");
    public static readonly Color BgSurface = (Color)ColorConverter.ConvertFromString("#161c2e");
    public static readonly Color BgPanel   = (Color)ColorConverter.ConvertFromString("#0f1420");
    public static readonly Color Fg        = (Color)ColorConverter.ConvertFromString("#b8c5d6");
    public static readonly Color FgDim     = (Color)ColorConverter.ConvertFromString("#4a5a6e");
    public static readonly Color FgBright  = (Color)ColorConverter.ConvertFromString("#e4eaf4");
    public static readonly Color Teal      = (Color)ColorConverter.ConvertFromString("#00e5c0");
    public static readonly Color TealDim   = (Color)ColorConverter.ConvertFromString("#005c4d");
    public static readonly Color Cyan      = (Color)ColorConverter.ConvertFromString("#5ec4ff");
    public static readonly Color Amber     = (Color)ColorConverter.ConvertFromString("#e5a000");
    public static readonly Color AmberDim  = (Color)ColorConverter.ConvertFromString("#1f1800");
    public static readonly Color Green     = (Color)ColorConverter.ConvertFromString("#7dff7d");
    public static readonly Color GreenDim  = (Color)ColorConverter.ConvertFromString("#1a3a1a");
    public static readonly Color Red       = (Color)ColorConverter.ConvertFromString("#ff5c5c");
    public static readonly Color Lavender  = (Color)ColorConverter.ConvertFromString("#b4a7d6");
    public static readonly Color Peach     = (Color)ColorConverter.ConvertFromString("#ffab91");

    // Brushes (frozen for perf)
    public static readonly SolidColorBrush BgBrush        = Freeze(new SolidColorBrush(Bg));
    public static readonly SolidColorBrush BgDarkBrush    = Freeze(new SolidColorBrush(BgDark));
    public static readonly SolidColorBrush BgSurfaceBrush = Freeze(new SolidColorBrush(BgSurface));
    public static readonly SolidColorBrush BgPanelBrush   = Freeze(new SolidColorBrush(BgPanel));
    public static readonly SolidColorBrush FgBrush        = Freeze(new SolidColorBrush(Fg));
    public static readonly SolidColorBrush FgDimBrush     = Freeze(new SolidColorBrush(FgDim));
    public static readonly SolidColorBrush FgBrightBrush  = Freeze(new SolidColorBrush(FgBright));
    public static readonly SolidColorBrush TealBrush      = Freeze(new SolidColorBrush(Teal));
    public static readonly SolidColorBrush TealDimBrush   = Freeze(new SolidColorBrush(TealDim));
    public static readonly SolidColorBrush CyanBrush      = Freeze(new SolidColorBrush(Cyan));
    public static readonly SolidColorBrush AmberBrush     = Freeze(new SolidColorBrush(Amber));
    public static readonly SolidColorBrush AmberDimBrush  = Freeze(new SolidColorBrush(AmberDim));
    public static readonly SolidColorBrush GreenBrush     = Freeze(new SolidColorBrush(Green));
    public static readonly SolidColorBrush GreenDimBrush  = Freeze(new SolidColorBrush(GreenDim));
    public static readonly SolidColorBrush RedBrush       = Freeze(new SolidColorBrush(Red));
    public static readonly SolidColorBrush LavenderBrush  = Freeze(new SolidColorBrush(Lavender));
    public static readonly SolidColorBrush PeachBrush     = Freeze(new SolidColorBrush(Peach));

    public const string FontUi   = "Bahnschrift";
    public const string FontMono = "Consolas";

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
