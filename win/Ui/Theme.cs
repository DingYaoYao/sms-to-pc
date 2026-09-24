using System.Drawing.Drawing2D;

namespace SmsLink.Ui;

/// <summary>统一的配色、字体和圆角绘制工具，界面各处都用它，避免风格漂移。</summary>
public static class Theme
{
    public static readonly Color Brand = Color.FromArgb(0x4F, 0x46, 0xE5);
    public static readonly Color BrandDark = Color.FromArgb(0x43, 0x38, 0xCA);
    public static readonly Color BrandSoft = Color.FromArgb(0xEE, 0xF0, 0xFE);
    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF5, 0xF8);
    public static readonly Color Card = Color.White;
    public static readonly Color Line = Color.FromArgb(0xE6, 0xE8, 0xEF);
    public static readonly Color Text = Color.FromArgb(0x1C, 0x1F, 0x26);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Ok = Color.FromArgb(0x16, 0xA3, 0x4A);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Danger = Color.FromArgb(0xDC, 0x26, 0x26);

    private static readonly string UiFamily = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
    private static readonly string MonoFamily = PickFamily("Consolas", "Cascadia Mono", "Courier New");

    private static string PickFamily(params string[] candidates)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in candidates)
        {
            if (installed.Contains(name)) return name;
        }
        return FontFamily.GenericSansSerif.Name;
    }

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) =>
        new(UiFamily, size, style, GraphicsUnit.Point);

    public static Font Mono(float size, FontStyle style = FontStyle.Bold) =>
        new(MonoFamily, size, style, GraphicsUnit.Point);

    public static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>四个角可以给不同半径——聊天气泡就是把左下角收小一点。</summary>
    public static GraphicsPath CornerPath(Rectangle rect, int topLeft, int topRight, int bottomRight, int bottomLeft)
    {
        var path = new GraphicsPath();
        var limit = Math.Max(1, Math.Min(rect.Width, rect.Height) / 2);
        int R(int value) => Math.Max(0, Math.Min(value, limit));

        var tl = R(topLeft);
        var tr = R(topRight);
        var br = R(bottomRight);
        var bl = R(bottomLeft);

        if (tl > 0) path.AddArc(rect.X, rect.Y, tl * 2, tl * 2, 180, 90);
        else path.AddLine(rect.X, rect.Y, rect.X, rect.Y);
        if (tr > 0) path.AddArc(rect.Right - tr * 2, rect.Y, tr * 2, tr * 2, 270, 90);
        if (br > 0) path.AddArc(rect.Right - br * 2, rect.Bottom - br * 2, br * 2, br * 2, 0, 90);
        if (bl > 0) path.AddArc(rect.X, rect.Bottom - bl * 2, bl * 2, bl * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillPath(Graphics g, GraphicsPath path, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokePath(Graphics g, GraphicsPath path, Color color, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void FillRounded(Graphics g, Rectangle rect, int radius, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Rectangle rect, int radius, Color color, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(rect, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void FillCircle(Graphics g, Rectangle rect, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, rect);
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }
}
