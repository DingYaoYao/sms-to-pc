using System.Drawing.Drawing2D;

namespace SmsLink.Ui;

/// <summary>
/// 程序图标。优先用编译进来的 app.ico（和 exe 图标同一个文件，含 16/20/24/32/48/64/128/256 八个尺寸），
/// 找不到资源时才用代码现画一个兜底。
/// </summary>
public static class AppIcon
{
    private static Icon? _window;
    private static Icon? _tray;

    /// <summary>窗口图标（标题栏），用 32px。</summary>
    public static Icon ForWindow() => _window ??= Load(SystemInformation.IconSize) ?? DrawFallback(64);

    /// <summary>托盘图标，必须用 16px，不然 Windows 会自己缩出毛边。</summary>
    public static Icon ForTray() => _tray ??= Load(SystemInformation.SmallIconSize) ?? DrawFallback(16);

    private static Icon? Load(Size size)
    {
        try
        {
            var assembly = typeof(AppIcon).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(item => item.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;

            using var stream = assembly.GetManifestResourceStream(name);
            return stream == null ? null : new Icon(stream, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>兜底：没有嵌入图标时现画一个（气泡里三个点，和小尺寸图标一致）。</summary>
    private static Icon DrawFallback(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            Theme.Prepare(g);

            using (var gradient = new LinearGradientBrush(
                       new Rectangle(0, 0, size, size),
                       Color.FromArgb(0x5B, 0x54, 0xEA),
                       Theme.BrandDark,
                       LinearGradientMode.ForwardDiagonal))
            {
                using var tile = Theme.RoundedPath(new Rectangle(0, 0, size - 1, size - 1), (int)(size * 0.22));
                g.FillPath(gradient, tile);
            }

            var bubble = new Rectangle(
                (int)(size * 0.12), (int)(size * 0.20),
                (int)(size * 0.76), (int)(size * 0.50));
            Theme.FillRounded(g, bubble, (int)(size * 0.13), Color.White);

            using (var tail = new GraphicsPath())
            {
                tail.AddPolygon(new[]
                {
                    new Point((int)(size * 0.22), (int)(size * 0.69)),
                    new Point((int)(size * 0.42), (int)(size * 0.69)),
                    new Point((int)(size * 0.22), (int)(size * 0.83)),
                });
                using var white = new SolidBrush(Color.White);
                g.FillPath(white, tail);
            }

            var dot = Math.Max(2, (int)(size * 0.12));
            for (var i = 0; i < 3; i++)
            {
                var x = (int)(size * 0.25) + i * (int)(size * 0.15);
                var y = (int)(size * 0.45) - dot / 2;
                Theme.FillCircle(g, new Rectangle(x, y, dot, dot), Theme.Brand);
            }
        }

        var handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        return (Icon)temp.Clone();
    }
}
