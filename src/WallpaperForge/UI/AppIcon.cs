using System.Drawing.Drawing2D;

namespace WallpaperForge.UI;

/// <summary>Ícono generado por código (no hace falta un .ico en el repo para la versión 0.1).</summary>
public static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
                Color.FromArgb(255, 124, 77, 255), Color.FromArgb(255, 0, 190, 220), 45f);
            using var path = RoundedRect(new Rectangle(2, 2, 60, 60), 14);
            g.FillPath(bg, path);
            // Montañas estilizadas (paisaje = "wallpaper")
            using var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillPolygon(white, new[] { new Point(8, 52), new Point(26, 26), new Point(38, 42), new Point(44, 34), new Point(58, 52) });
            g.FillEllipse(white, 40, 10, 12, 12);
        }
        _icon = Icon.FromHandle(bmp.GetHicon());
        return _icon;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
