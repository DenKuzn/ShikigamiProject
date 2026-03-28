using System.Windows.Media;

namespace Shikigami.Runner.Theme;

/// <summary>
/// Jujutsu Kaisen "Domain Expansion" color palette.
/// Dark occult void with cursed energy violet, malevolent crimson, and infinity blue.
/// </summary>
public static class DeepSpaceTheme
{
    // ─── The Void (Domain Interior) ─────────────────────────
    public static readonly Color Bg        = (Color)ColorConverter.ConvertFromString("#08060F");
    public static readonly Color BgDark    = (Color)ColorConverter.ConvertFromString("#04030A");
    public static readonly Color BgSurface = (Color)ColorConverter.ConvertFromString("#13101E");
    public static readonly Color BgPanel   = (Color)ColorConverter.ConvertFromString("#0D0A17");

    // ─── Text — neutral silver (readable against void) ─────
    public static readonly Color Fg        = (Color)ColorConverter.ConvertFromString("#B8C2D0");
    public static readonly Color FgDim     = (Color)ColorConverter.ConvertFromString("#4A3D65");
    public static readonly Color FgBright  = (Color)ColorConverter.ConvertFromString("#E4E8F0");

    // ─── Cursed Energy (呪力) — primary accent ──────────────
    public static readonly Color Teal      = (Color)ColorConverter.ConvertFromString("#8B5CF6");
    public static readonly Color TealDim   = (Color)ColorConverter.ConvertFromString("#2D1B69");

    // ─── Infinity Blue (無下限) — tools & techniques ────────
    public static readonly Color Cyan      = (Color)ColorConverter.ConvertFromString("#60A5FA");

    // ─── Cursed Flame (呪炎) — warnings ─────────────────────
    public static readonly Color Amber     = (Color)ColorConverter.ConvertFromString("#F59E0B");
    public static readonly Color AmberDim  = (Color)ColorConverter.ConvertFromString("#2D1F05");

    // ─── Reverse Cursed Technique (反転術式) — success ──────
    public static readonly Color Green     = (Color)ColorConverter.ConvertFromString("#34D399");
    public static readonly Color GreenDim  = (Color)ColorConverter.ConvertFromString("#0A2E1F");

    // ─── Malevolent (宿儺) — danger ─────────────────────────
    public static readonly Color Red       = (Color)ColorConverter.ConvertFromString("#EF4444");

    // ─── Special Accents ────────────────────────────────────
    public static readonly Color Lavender  = (Color)ColorConverter.ConvertFromString("#A78BFA");
    public static readonly Color Peach     = (Color)ColorConverter.ConvertFromString("#D4A574");

    // ─── Brushes (frozen for perf) ──────────────────────────
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

    public const string FontUi   = "Yu Gothic UI";
    public const string FontMono = "Consolas";

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
