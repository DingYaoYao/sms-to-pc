using SmsLink.Protocol;

namespace SmsLink.Ui;

/// <summary>
/// 一条短信，画成聊天气泡：左边对齐、宽度跟着内容走、左下角收小一点。
/// 带验证码的用淡紫底并把验证码放大显示，右边带"复制"。
/// </summary>
public sealed class MessageCard : Control
{
    private const int PadX = 14;
    private const int PadY = 12;
    private const int HeaderHeight = 22;
    private const int CodeRowHeight = 34;
    private const int MaxDisplayChars = 600;
    private const int MinBubbleWidth = 150;
    private const int RightGutter = 44;   // 右侧留白，让气泡看得出"是靠左的"

    private static readonly TextFormatFlags WrapFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Left;

    private readonly string _displayBody;
    private Rectangle _copyRect;
    private bool _hoverCopy;

    public SmsMessage Message { get; }
    public event Action<string>? CopyRequested;

    public MessageCard(SmsMessage message)
    {
        Message = message;
        _displayBody = Trim(message.Body);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        // 气泡自己画圆角，控件底色跟列表背景一致
        BackColor = Theme.Bg;
        Cursor = message.Code is { Length: > 0 } ? Cursors.Hand : Cursors.Default;
    }

    private static string Trim(string body) =>
        body.Length > MaxDisplayChars ? body[..MaxDisplayChars] + "…" : body;

    /// <summary>气泡宽度：跟着内容走，但不超过可用宽度减去右侧留白。</summary>
    public static int MeasureBubbleWidth(SmsMessage message, int availableWidth)
    {
        var max = Math.Max(MinBubbleWidth, availableWidth - RightGutter);

        using var headerFont = Theme.Ui(9f, FontStyle.Bold);
        using var bodyFont = Theme.Ui(9.5f);
        using var timeFont = Theme.Ui(8.5f);

        var headerWidth = TextRenderer.MeasureText(message.From, headerFont, new Size(int.MaxValue, int.MaxValue),
                              TextFormatFlags.NoPadding).Width
                          + TextRenderer.MeasureText(RelativeTime(message.Timestamp), timeFont,
                              new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width
                          + 40;

        var bodyWidth = 0;
        foreach (var line in Trim(message.Body).Split('\n'))
        {
            var width = TextRenderer.MeasureText(line, bodyFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width;
            bodyWidth = Math.Max(bodyWidth, width);
        }

        var codeWidth = 0;
        if (message.Code is { Length: > 0 })
        {
            using var codeFont = Theme.Mono(15f);
            codeWidth = TextRenderer.MeasureText(message.Code, codeFont, new Size(int.MaxValue, int.MaxValue),
                            TextFormatFlags.NoPadding).Width
                        + TextRenderer.MeasureText("复制", timeFont, new Size(int.MaxValue, int.MaxValue),
                            TextFormatFlags.NoPadding).Width
                        + 60;
        }

        var desired = PadX * 2 + Math.Max(Math.Max(headerWidth, bodyWidth), codeWidth);
        return Math.Clamp(desired, MinBubbleWidth, max);
    }

    public static int MeasureHeight(SmsMessage message, int bubbleWidth)
    {
        var body = Trim(message.Body);
        var bodyHeight = MeasureBodyHeight(body, bubbleWidth - PadX * 2);
        var height = PadY + HeaderHeight + bodyHeight + PadY + 2;
        if (!string.IsNullOrEmpty(message.Code)) height += CodeRowHeight;
        return height;
    }

    private static int MeasureBodyHeight(string body, int width)
    {
        using var font = Theme.Ui(9.5f);
        var size = TextRenderer.MeasureText(
            body, font, new Size(Math.Max(40, width), int.MaxValue), WrapFlags);
        return size.Height;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = Message.Code is { Length: > 0 } && _copyRect.Contains(e.Location);
        if (hover != _hoverCopy)
        {
            _hoverCopy = hover;
            Cursor = hover ? Cursors.Hand : Message.Code is { Length: > 0 } ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Message.Code is { Length: > 0 } code) CopyRequested?.Invoke(code);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        var g = e.Graphics;
        var hasCode = Message.Code is { Length: > 0 };

        // 气泡本体：四个角圆角，左下角收小，看起来像聊天窗口里的来消息
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.CornerPath(rect, 14, 14, 14, 4))
        {
            Theme.FillPath(g, path, hasCode ? Color.FromArgb(0xEC, 0xEF, 0xFE) : Color.White);
            Theme.StrokePath(g, path, hasCode ? Color.FromArgb(0xD8, 0xDD, 0xFB) : Theme.Line);
        }

        // 第一行：发送方 + 时间
        var inner = new Rectangle(PadX, PadY, Width - PadX * 2, HeaderHeight);
        using (var senderFont = Theme.Ui(9f, FontStyle.Bold))
        {
            TextRenderer.DrawText(
                g, Message.From, senderFont, inner, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        using (var timeFont = Theme.Ui(8.5f))
        {
            TextRenderer.DrawText(
                g, RelativeTime(Message.Timestamp), timeFont, inner, Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // 正文
        var bodyTop = PadY + HeaderHeight;
        var bodyHeight = hasCode ? Height - bodyTop - CodeRowHeight - 6 : Height - bodyTop - PadY;
        using (var bodyFont = Theme.Ui(9.5f))
        {
            TextRenderer.DrawText(
                g, _displayBody, bodyFont,
                new Rectangle(PadX, bodyTop, Width - PadX * 2, Math.Max(16, bodyHeight)),
                Theme.Text, WrapFlags);
        }

        // 验证码行
        if (hasCode)
        {
            var top = Height - CodeRowHeight - PadY / 2;
            using (var codeFont = Theme.Mono(15f))
            {
                TextRenderer.DrawText(
                    g, Message.Code, codeFont,
                    new Rectangle(PadX - 2, top, Width, CodeRowHeight), Theme.Brand,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            using var chipFont = Theme.Ui(8.5f);
            var chipText = "复制";
            var chipSize = TextRenderer.MeasureText(chipText, chipFont);
            _copyRect = new Rectangle(
                Width - PadX - chipSize.Width - 18,
                top + (CodeRowHeight - 24) / 2,
                chipSize.Width + 18,
                24);
            using (var chipPath = Theme.CornerPath(_copyRect, 12, 12, 12, 12))
            {
                Theme.FillPath(g, chipPath, _hoverCopy ? Theme.Brand : Color.White);
                Theme.StrokePath(g, chipPath, _hoverCopy ? Theme.Brand : Color.FromArgb(0xD8, 0xDD, 0xFB));
            }
            TextRenderer.DrawText(
                g, chipText, chipFont, _copyRect,
                _hoverCopy ? Color.White : Theme.Brand,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            _copyRect = Rectangle.Empty;
        }
    }

    private static string RelativeTime(long timestamp)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime();
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("M月d日 HH:mm");
    }
}
