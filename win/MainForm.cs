using System.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SmsLink.Protocol;
using SmsLink.Ui;

namespace SmsLink;

/// <summary>
/// 无边框、固定尺寸、启动就在屏幕右下角的小窗口。
/// 只做三件事：显示配对码、显示收到的短信、把验证码复制到剪贴板。
/// </summary>
public sealed class MainForm : Form
{
    private const int BaseWidth = 420;
    private const int BaseHeight = 600;
    private const int MaxCards = 100;
    private const int PairCodeLength = 4;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SmsLink";

    private readonly AppConfig _config;
    private readonly List<SmsMessage> _messages;
    private float _scale;
    private static readonly bool LayoutDump =
        Environment.GetEnvironmentVariable("SMSLINK_LAYOUT_DUMP") == "1";

    private SmsServer? _server;
    private Discovery? _discovery;

    private readonly Panel _header = new();
    private readonly FlatButton _buttonMinimize = new();
    private readonly FlatButton _buttonAbout = new();
    private readonly Panel _pairPanel = new();
    private readonly Panel _connectedPanel = new();
    private readonly Label _codeLabel = new();
    private readonly Label _stepsLabel = new();
    private readonly Label _addressLabel = new();
    private readonly Label _connectedSub = new();
    private readonly Button _buttonCopyCode = new();
    private readonly Button _buttonNewCode = new();
    private readonly Button _buttonUnpair = new();
    private readonly FlowLayoutPanel _messageList = new();
    private readonly Label _emptyHint = new();
    private readonly Panel _footer = new();
    private readonly CheckBox _checkAutostart = new();
    private readonly CheckBox _checkAutoCopy = new();
    private readonly Button _buttonClear = new();
    private readonly Label _footerNote = new();
    private NotifyIcon _tray = null!;
    private ContextMenuStrip _trayMenu = null!;
    private bool _dragging;
    private Point _dragOrigin;
    private SmsMessage? _lastNotified;
    private string _statusText = "等待手机配对";
    private Color _statusColor = Theme.Warn;

    public MainForm(AppConfig config, List<SmsMessage> messages, bool startServer = true)
    {
        _config = config;
        _messages = messages;

        using (var graphics = CreateGraphics())
        {
            _scale = graphics.DpiX / 96f;
        }

        Text = "码上来";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;   // 无边框
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Bg;
        Font = Theme.Ui(9f);
        Icon = AppIcon.ForWindow();

        var size = new Size(S(BaseWidth), S(BaseHeight));
        ClientSize = size;
        MinimumSize = size;   // 固定尺寸，拖不动大小
        MaximumSize = size;

        BuildUi();
        BuildTray();

        // 程序改过名字或挪过位置时，让开机自启指向当前这个 exe，否则开机起的是旧路径。
        if (_config.StartWithWindows) ApplyAutostart(true);

        if (startServer) StartServer();
        RefreshState();
        RenderMessages();
    }

    private int S(int value) => (int)Math.Round(value * _scale);

    public string DescribeLayout()
    {
        var card = _messageList.Controls.OfType<MessageCard>().FirstOrDefault();
        return $"form.Client={ClientSize.Width}x{ClientSize.Height} scale={_scale:0.00} " +
               $"list.Bounds={_messageList.Bounds} list.Client={_messageList.ClientSize} " +
               $"card={(card == null ? "none" : $"{card.Width}x{card.Height}")} cards={_messageList.Controls.Count}";
    }

    /// <summary>调试用：把布局缩放钉死后重排，让离屏截图和真实窗口一致。</summary>
    public void ForceScale(float scale)
    {
        _scale = scale;
        var size = new Size(S(BaseWidth), S(BaseHeight));
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        ClientSize = size;
        MinimumSize = size;
        MaximumSize = size;
        DoLayout();
        RenderMessages();
        Application.DoEvents();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW，无边框窗口加一层投影
            return parameters;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 2; // DWMWCP_ROUND，Windows 11 圆角
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // 旧系统没这个属性，忽略
        }
    }

    /* ---------------- 界面 ---------------- */

    private void BuildUi()
    {
        // 顶部：标题 + 连接状态 + 最小化/关于（整条都可拖动，只画一条分割线）
        _header.BackColor = Color.White;
        _header.Paint += HeaderPaint;
        _header.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = new Point(e.X, e.Y);
        };
        _header.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var point = _header.PointToScreen(new Point(e.X, e.Y));
            Location = new Point(point.X - _dragOrigin.X, point.Y - _dragOrigin.Y);
        };
        _header.MouseUp += (_, _) => _dragging = false;

        _buttonMinimize.Text = "—";
        _buttonMinimize.Style = FlatButton.Look.Ghost;
        _buttonMinimize.Font = Theme.Ui(9f);
        _buttonMinimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _buttonMinimize.Cursor = Cursors.Hand;
        _header.Controls.Add(_buttonMinimize);

        _buttonAbout.Text = "关于";
        _buttonAbout.Style = FlatButton.Look.Ghost;
        _buttonAbout.Font = Theme.Ui(8.5f);
        _buttonAbout.Click += (_, _) =>
        {
            using var about = new AboutForm();
            about.ShowDialog(this);
        };
        _buttonAbout.Cursor = Cursors.Hand;
        _header.Controls.Add(_buttonAbout);

        Controls.Add(_header);

        // 未配对：显示配对码
        _pairPanel.BackColor = Color.White;
        _codeLabel.AutoSize = false;
        _codeLabel.BackColor = Theme.BrandSoft;
        _codeLabel.ForeColor = Theme.BrandDark;
        _codeLabel.Font = Theme.Mono(28f);
        _codeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _stepsLabel.AutoSize = false;
        _stepsLabel.ForeColor = Theme.Muted;
        _stepsLabel.Text = "1. 手机装好“码上来”并打开\n2. 确认手机和电脑连的是同一个 WiFi\n3. 点“配对电脑”，输入上面的 4 位数字";
        _addressLabel.AutoSize = false;
        _addressLabel.ForeColor = Theme.Muted;
        ConfigureButton(_buttonCopyCode, "复制配对码");
        ConfigureButton(_buttonNewCode, "换一个配对码");
        _buttonCopyCode.Click += (_, _) => CopyToClipboard(_config.PairCode, "配对码");
        _buttonNewCode.Click += (_, _) =>
        {
            _config.PairCode = Crypto.RandomPairCode();
            _config.Save();
            RefreshState();
        };
        _pairPanel.Controls.AddRange([_codeLabel, _stepsLabel, _addressLabel, _buttonCopyCode, _buttonNewCode]);
        Controls.Add(_pairPanel);

        // 已配对：状态条上已经写了"已连接到 xxx"，这里只留一行说明和解除按钮
        _connectedPanel.BackColor = Color.White;
        _connectedSub.AutoSize = false;
        _connectedSub.ForeColor = Theme.Muted;
        _connectedSub.Text = "手机收到短信会自动显示在下面";
        ConfigureButton(_buttonUnpair, "解除配对");
        _buttonUnpair.Click += (_, _) => UnpairAll();
        _connectedPanel.Controls.AddRange([_connectedSub, _buttonUnpair]);
        Controls.Add(_connectedPanel);

        // 短信列表
        _messageList.BackColor = Theme.Bg;
        _messageList.FlowDirection = FlowDirection.TopDown;
        _messageList.WrapContents = false;
        _messageList.AutoScroll = true;
        _messageList.Padding = new Padding(0);
        _messageList.ClientSizeChanged += (_, _) => LayoutCards();
        Controls.Add(_messageList);

        _emptyHint.AutoSize = false;
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = Theme.Muted;
        Controls.Add(_emptyHint);

        // 底部设置
        _footer.BackColor = Color.White;
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };

        _checkAutostart.AutoSize = true;
        _checkAutostart.Text = "开机自启";
        _checkAutostart.CheckedChanged += (_, _) =>
        {
            _config.StartWithWindows = _checkAutostart.Checked;
            ApplyAutostart(_config.StartWithWindows);
            _config.Save();
        };

        _checkAutoCopy.AutoSize = true;
        _checkAutoCopy.Text = "自动复制验证码";
        _checkAutoCopy.CheckedChanged += (_, _) =>
        {
            _config.AutoCopy = _checkAutoCopy.Checked;
            _config.Save();
        };

        ConfigureButton(_buttonClear, "清空记录");
        _buttonClear.Click += (_, _) => ClearMessages();

        _footerNote.AutoSize = false;
        _footerNote.ForeColor = Theme.Muted;
        _footerNote.Text = "短信只在局域网内加密传输，不经过任何服务器";

        _footer.Controls.AddRange([_checkAutostart, _checkAutoCopy, _buttonClear, _footerNote]);
        Controls.Add(_footer);

        // WinForms 里先添加的控件在上层，列表必须显式提到最前面，否则会被白底面板盖住。
        _messageList.BringToFront();
        _emptyHint.BringToFront();
    }

    private void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.System;
        button.UseVisualStyleBackColor = true;
    }

    /// <summary>标题、连接状态、按钮都在这一条里，下面只画一条分割线。</summary>
    private void HeaderPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prepare(g);

        using (var pen = new Pen(Theme.Line))
        {
            g.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        }

        using var titleFont = Theme.Ui(10.5f, FontStyle.Bold);
        var titleSize = TextRenderer.MeasureText(
            g, "码上来", titleFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        var left = S(16);
        TextRenderer.DrawText(
            g, "码上来", titleFont,
            new Rectangle(left, 0, titleSize.Width + S(2), _header.Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var dotSize = S(8);
        var dotX = left + titleSize.Width + S(14);
        Theme.FillCircle(
            g, new Rectangle(dotX, _header.Height / 2 - dotSize / 2, dotSize, dotSize), _statusColor);

        using var statusFont = Theme.Ui(9f);
        var statusLeft = dotX + dotSize + S(6);
        var statusWidth = Math.Max(S(60), _header.Width - statusLeft - S(104));
        TextRenderer.DrawText(
            g, _statusText, statusFont,
            new Rectangle(statusLeft, 0, statusWidth, _header.Height), _statusColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DoLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MoveToBottomRight();
        DoLayout();
        // 窗口完全定型后再纠正一次位置，否则用的是还没最终生效的高度，落点会偏。
        BeginInvoke(MoveToBottomRight);
    }

    /// <summary>每次打开都出现在屏幕右下角。</summary>
    private void MoveToBottomRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
        Location = new Point(
            area.Right - Width - S(20),
            area.Bottom - Height - S(28));
    }

    private void DoLayout()
    {
        var width = ClientSize.Width;
        var pad = S(16);
        var y = 0;

        _header.SetBounds(0, y, width, S(46));
        // 顺序和系统一致：最小化在左，"关于"接管原来的关闭位置
        _buttonMinimize.SetBounds(width - S(100), S(9), S(32), S(28));
        _buttonAbout.SetBounds(width - S(58), S(9), S(48), S(28));
        y += _header.Height;

        var bodyTop = y;
        var footerHeight = S(104);
        var paired = _config.Links.Count > 0;
        var panelHeight = paired ? S(56) : S(238);
        var bodyHeight = Math.Max(S(120), ClientSize.Height - bodyTop - footerHeight);

        _pairPanel.SetBounds(0, bodyTop, width, panelHeight);
        _connectedPanel.SetBounds(0, bodyTop, width, panelHeight);

        if (paired)
        {
            _connectedSub.SetBounds(pad, S(14), width - pad * 2 - S(104), S(28));
            _buttonUnpair.SetBounds(width - pad - S(96), S(12), S(96), S(30));
        }
        else
        {
            _codeLabel.SetBounds(pad + S(30), S(14), width - (pad + S(30)) * 2, S(72));
            _stepsLabel.SetBounds(pad + S(30), S(98), width - (pad + S(30)) * 2, S(66));
            _addressLabel.SetBounds(pad + S(30), S(168), width - (pad + S(30)) * 2, S(22));
            _buttonCopyCode.SetBounds(width - pad - S(216), S(194), S(104), S(30));
            _buttonNewCode.SetBounds(width - pad - S(104), S(194), S(104), S(30));
        }

        _messageList.SetBounds(pad, bodyTop + panelHeight, width - pad * 2, bodyHeight - panelHeight);
        _emptyHint.SetBounds(pad, bodyTop + panelHeight, width - pad * 2, bodyHeight - panelHeight);

        _footer.SetBounds(0, ClientSize.Height - footerHeight, width, footerHeight);
        _checkAutostart.SetBounds(pad, S(16), S(110), S(24));
        _checkAutoCopy.SetBounds(pad + S(116), S(16), S(150), S(24));
        _buttonClear.SetBounds(width - pad - S(90), S(12), S(90), S(30));
        _footerNote.SetBounds(pad, S(50), width - pad * 2, S(22));

        LayoutCards();
    }

    /* ---------------- 数据与状态 ---------------- */

    private void StartServer()
    {
        try
        {
            _server = new SmsServer(_config, _messages);
            _server.MessageReceived += message => BeginInvoke(() => OnMessage(message));
            _server.LinksChanged += () => BeginInvoke(RefreshState);
            _server.Start();

            _discovery = new Discovery(_config);
            _discovery.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"启动失败：{error.Message}\n\n如果提示端口被占用，请先关掉已经在运行的码上来。",
                "码上来", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshState()
    {
        var paired = _config.Links.Count > 0;
        var name = paired ? _config.Links[^1].Name : "";

        _statusText = paired ? $"已连接到 {name}" : "等待手机配对";
        _statusColor = paired ? Theme.Ok : Theme.Warn;
        _header.Invalidate();

        _pairPanel.Visible = !paired;
        _connectedPanel.Visible = paired;

        if (!paired)
        {
            _codeLabel.Text = string.Join("  ", _config.PairCode.PadLeft(PairCodeLength, '0').ToCharArray());
            var addresses = AppConfig.LocalIPv4();
            _addressLabel.Text = addresses.Count > 0
                ? "本机地址 " + string.Join(" / ", addresses)
                : "没有检测到局域网地址，请检查网络连接";
            _emptyHint.Text = "配对成功后，手机上收到的短信会显示在这里";
        }
        else
        {
            _emptyHint.Text = "还没有短信\n手机收到新短信后会自动出现在这里";
        }

        _checkAutostart.Checked = _config.StartWithWindows;
        _checkAutoCopy.Checked = _config.AutoCopy;

        DoLayout();
    }

    private void RenderMessages()
    {
        _messageList.SuspendLayout();
        _messageList.Controls.Clear();
        foreach (var message in _messages.Take(MaxCards))
        {
            _messageList.Controls.Add(CreateCard(message));
        }
        _messageList.ResumeLayout();
        LayoutCards();
        UpdateEmptyHint();
    }

    private MessageCard CreateCard(SmsMessage message)
    {
        var card = new MessageCard(message);
        card.CopyRequested += code => CopyToClipboard(code, "验证码");
        return card;
    }

    private void LayoutCards()
    {
        // 用控件自身的 Bounds 宽度算，不用 ClientSize：滚动条和 DPI 虚拟化都会让两者对不上。
        var available = Math.Max(S(180), _messageList.Width - S(4));
        foreach (Control control in _messageList.Controls)
        {
            if (control is not MessageCard card) continue;
            card.Width = MessageCard.MeasureBubbleWidth(card.Message, available);
            card.Height = MessageCard.MeasureHeight(card.Message, card.Width);
            card.Margin = new Padding(0, 0, 0, S(8));
        }
        if (LayoutDump)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "smslink-layout.txt"),
                    DescribeLayout() + Environment.NewLine);
            }
            catch
            {
                // 调试输出失败无所谓
            }
        }
    }

    private void UpdateEmptyHint()
    {
        _emptyHint.Visible = _messages.Count == 0;
    }

    private void OnMessage(SmsMessage message)
    {
        // 窗口在后台时不抢剪贴板，避免打断用户正在复制的东西。
        if (_config.AutoCopy && !string.IsNullOrEmpty(message.Code) && Visible && WindowState != FormWindowState.Minimized)
        {
            CopyToClipboard(message.Code!, "验证码", silent: true);
        }

        RenderMessages();

        if (_config.Sound)
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch
            {
                // 没有音频设备时忽略
            }
        }

        try
        {
            _lastNotified = message;
            _tray.BalloonTipTitle = string.IsNullOrEmpty(message.Code)
                ? message.From
                : $"{message.From} · 验证码 {message.Code}";
            _tray.BalloonTipText = message.Body.Length > 120 ? message.Body[..120] + "…" : message.Body;
            _tray.ShowBalloonTip(4000);
        }
        catch
        {
            // 通知失败不影响收短信
        }
    }

    private void CopyToClipboard(string text, string label, bool silent = false)
    {
        try
        {
            Clipboard.SetText(text);
            if (!silent) ShowTrayTip($"{label}已复制");
        }
        catch
        {
            if (!silent) ShowTrayTip("复制失败，请重试");
        }
    }

    private void ShowTrayTip(string text)
    {
        _tray.BalloonTipTitle = "码上来";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(2000);
    }

    private void ClearMessages()
    {
        _messages.Clear();
        _config.SaveMessages(_messages);
        RenderMessages();
    }

    private void UnpairAll()
    {
        if (MessageBox.Show("解除后手机需要重新配对，确定吗？", "码上来",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _config.Links.Clear();
        _config.Save();
        RefreshState();
    }

    /* ---------------- 托盘与开机自启 ---------------- */

    private void BuildTray()
    {
        _trayMenu = new ContextMenuStrip { Font = Theme.Ui(9f) };
        _trayMenu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add("复制配对码", null, (_, _) => CopyToClipboard(_config.PairCode, "配对码"));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.ForTray(),
            Text = "码上来",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        // 点通知：打开窗口并复制这条短信的验证码
        _tray.BalloonTipClicked += (_, _) => OnNotificationClicked();
    }

    private void OnNotificationClicked()
    {
        ShowFromTray();
        var code = _lastNotified?.Code;
        if (string.IsNullOrEmpty(code)) return;

        CopyToClipboard(code!, "验证码", silent: true);
        ShowTrayTip($"验证码 {code} 已复制");
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        MoveToBottomRight();
        BringToFront();
        Activate();
    }

    private void ExitApp()
    {
        _server?.Dispose();
        _discovery?.Dispose();
        _tray.Visible = false;
        Application.Exit();
    }

    private static void ApplyAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写入失败不影响主功能
        }
    }

    public static bool AutostartRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _server?.Dispose();
        _discovery?.Dispose();
        if (_tray != null) _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server?.Dispose();
            _discovery?.Dispose();
            _tray?.Dispose();
            _trayMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
