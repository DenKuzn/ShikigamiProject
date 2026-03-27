using System.Drawing;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Shikigami.Server.Ui;

/// <summary>
/// Renders an emoji string into a System.Drawing.Icon and a WPF ImageSource.
/// </summary>
public static class EmojiIcon
{
    private const string Emoji = "\U0001F407"; // 🐇

    public static Icon CreateDrawingIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var font = new Font("Segoe UI Emoji", size * 0.7f, GraphicsUnit.Pixel);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Emoji, font, Brushes.White, new RectangleF(0, 0, size, size), sf);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public static BitmapSource CreateWpfIcon(int size = 32)
    {
        var icon = CreateDrawingIcon(size);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
