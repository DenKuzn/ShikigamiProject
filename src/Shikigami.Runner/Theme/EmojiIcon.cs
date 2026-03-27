using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shikigami.Runner.Theme;

/// <summary>
/// Renders the 🐇 emoji into a WPF BitmapSource for use as a window icon.
/// Pure WPF implementation — no System.Drawing / WinForms dependency.
/// </summary>
public static class EmojiIcon
{
    private const string Emoji = "\U0001F407"; // 🐇

    public static BitmapSource Create(int size = 32)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var text = new FormattedText(
                Emoji,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"),
                size * 0.75,
                Brushes.White,
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
