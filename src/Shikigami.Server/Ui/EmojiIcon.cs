using System.Drawing;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Shikigami.Server.Ui;

/// <summary>
/// Renders the 呪 (curse) kanji into icons for window title bar and system tray.
/// Jujutsu Kaisen themed — purple cursed energy.
/// </summary>
public static class EmojiIcon
{
    private const string CurseKanji = "\u546A"; // 呪

    public static Icon CreateDrawingIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var font = new Font("Yu Gothic UI", size * 0.65f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(167, 139, 250)); // #A78BFA lavender
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(CurseKanji, font, brush, new RectangleF(0, 0, size, size), sf);

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
