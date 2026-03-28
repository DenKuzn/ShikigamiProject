using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shikigami.Runner.Theme;

/// <summary>
/// Renders the 呪 (curse) kanji into a WPF BitmapSource for use as a window icon.
/// Jujutsu Kaisen themed — purple cursed energy glow.
/// </summary>
public static class EmojiIcon
{
    private const string CurseKanji = "\u546A"; // 呪

    public static BitmapSource Create(int size = 32)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Background glow circle
            var glowBrush = new RadialGradientBrush(
                Color.FromArgb(80, 139, 92, 246),  // #8B5CF6 at 31% opacity
                Colors.Transparent);
            dc.DrawEllipse(glowBrush, null,
                new Point(size / 2.0, size / 2.0),
                size * 0.45, size * 0.45);

            // Curse kanji
            var text = new FormattedText(
                CurseKanji,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.65,
                new SolidColorBrush(Color.FromRgb(167, 139, 250)),  // #A78BFA lavender
                96);

            var x = (size - text.Width) / 2;
            var y = (size - text.Height) / 2;
            dc.DrawText(text, new Point(x, y));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
