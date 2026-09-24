namespace SmsLink.Ui;

/// <summary>
/// 「关于」窗口：开发者信息、声明、联系方式，以及微信赞赏码。
/// 赞赏码优先读程序目录下的 about-qr.png（可随时替换），没有就用编译进 exe 的那张。
/// </summary>
public sealed class AboutForm : Form
{
    private const string AppTitle = "码上来";
    private const string Tagline = "手机收到短信，电脑上直接看";
    private const string Developer = "开发者：Otemain";
    private const string Contact = "邮箱：dog8520963@163.com";
    /// <summary>GitHub 地址；留空则显示"（待填写）"，填了以后是可点击的链接。</summary>
    private const string GithubUrl = "https://github.com/DingYaoYao/sms-to-pc";
    private const string Statement =
        "声明：本软件只在你自己的手机和电脑之间通过局域网传输短信，不经过任何服务器，" +
        "也不会收集或上传任何数据；短信内容仅保存在你自己的设备上。\n\n" +
        "请仅用于接收本人手机的短信。用于监控他人设备可能违反法律，后果自负。\n\n" +
        "软件按“现状”提供，不对使用造成的任何损失承担责任。";

    private readonly Panel _qrBox = new();
    private readonly float _scale;

    public AboutForm()
    {
        using (var graphics = CreateGraphics())
        {
            _scale = graphics.DpiX / 96f;
        }

        Text = "关于";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = Theme.Ui(9f);
        Icon = AppIcon.ForWindow();
        ClientSize = new Size(S(420), S(756));

        var y = S(20);

        // .NET 会把 git 提交号拼在版本号后面（1.1.0+59070bf…），这里只留版本号本身
        var version = Application.ProductVersion.Split('+')[0];
        AddLabel($"{AppTitle}  v{version}", Theme.Ui(15f, FontStyle.Bold), Theme.Text, ref y, S(32));
        AddLabel(Tagline, Theme.Ui(9f), Theme.Muted, ref y, S(24));

        y += S(10);
        _qrBox.SetBounds(S(30), y, S(360), S(300));
        _qrBox.BackColor = Color.White;
        _qrBox.Paint += (_, e) => DrawQr(e.Graphics);
        Controls.Add(_qrBox);
        y += S(312);

        AddLabel("如果这个小工具帮到了你，可以扫码支持一下：", Theme.Ui(9f), Theme.Muted, ref y, S(24));
        y += S(6);
        AddLabel(Developer, Theme.Ui(9f), Theme.Text, ref y, S(24));
        AddLabel(Contact, Theme.Ui(9f), Theme.Text, ref y, S(24));
        AddGithubRow(ref y);
        y += S(6);

        var statement = new Label
        {
            Text = Statement,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
        };
        statement.SetBounds(S(30), y, S(360), S(160));
        Controls.Add(statement);
        y += S(168);

        var hint = new Label
        {
            Text = "关闭本窗口请用托盘图标右键 → 退出",
            Font = Theme.Ui(8f),
            ForeColor = Theme.Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        hint.SetBounds(S(30), y, S(240), S(32));
        Controls.Add(hint);

        var ok = new Button
        {
            Text = "确定",
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.OK,
        };
        ok.SetBounds(S(310), y, S(80), S(32));
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }

    private void AddLabel(string text, Font font, Color color, ref int y, int height)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = false,
            TextAlign = text.Contains('\n') ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
        };
        label.SetBounds(S(30), y, S(360), height);
        Controls.Add(label);
        y += height;
    }

    private int S(int value) => (int)Math.Round(value * _scale);

    private void DrawQr(Graphics graphics)
    {
        Theme.Prepare(graphics);
        var box = new Rectangle(0, 0, _qrBox.Width - 1, _qrBox.Height - 1);

        using var image = LoadQrImage();
        if (image != null)
        {
            var size = Math.Min(box.Width, box.Height) - S(10);
            var target = new Rectangle((box.Width - size) / 2, (box.Height - size) / 2, size, size);
            graphics.DrawImage(image, target);
            // 二维码本身是白底，给它描一圈浅边，否则和白对话框糊在一起
            Theme.StrokeRounded(graphics, Rectangle.Inflate(target, S(3), S(3)), S(6), Theme.Line);
            return;
        }

        using (var path = Theme.RoundedPath(box, 12))
        {
            Theme.FillPath(graphics, path, Color.FromArgb(0xF7, 0xF8, 0xFB));
            Theme.StrokePath(graphics, path, Theme.Line);
        }
        using var font = Theme.Ui(9f);
        TextRenderer.DrawText(
            graphics,
            "（赞赏码图片位置）\n把 about-qr.png 放到程序目录，或用编译进来的那张",
            font, box, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private static Image? LoadQrImage()
    {
        // 1) 程序目录里的图片，方便随时替换
        foreach (var name in new[] { "about-qr.png", "about-qr.jpg", "赞赏码.png", "赞赏码.jpg" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                return Image.FromFile(path);
            }
            catch
            {
                // 图片坏了就当没有
            }
        }

        // 2) 编译进 exe 的资源
        try
        {
            var assembly = typeof(AboutForm).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(item => item.EndsWith("about-qr.png", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null) return Image.FromStream(stream);
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private void AddGithubRow(ref int y)
    {
        var hasUrl = GithubUrl.Length > 0;
        var label = new Label
        {
            Text = hasUrl ? "GitHub：" + GithubUrl.Replace("https://", "") : "GitHub：（待填写）",
            Font = Theme.Ui(9f),
            ForeColor = hasUrl ? Theme.Brand : Theme.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = hasUrl ? Cursors.Hand : Cursors.Default,
        };
        label.SetBounds(S(30), y, S(360), S(24));
        if (hasUrl)
        {
            label.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(GithubUrl) { UseShellExecute = true });
                }
                catch
                {
                    // 打不开浏览器就算了
                }
            };
        }
        Controls.Add(label);
        y += S(24);
    }
}
