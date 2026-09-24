using System.Text.RegularExpressions;

namespace SmsLink.Protocol;

/// <summary>从短信正文里挑出最像验证码的那串数字。</summary>
public static partial class CodeExtractor
{
    private static readonly string[] Keywords =
    [
        "验证码", "校验码", "动态码", "短信密码", "登录密码", "登陆密码", "密码",
        "口令", "确认码", "提取码", "取件码", "口令码", "code", "Code", "CODE", "OTP", "otp", "PIN",
    ];

    [GeneratedRegex(@"(?<!\d)(\d{4,8})(?!\d)")]
    private static partial Regex Digits();

    public static string? Extract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var matches = Digits().Matches(body);
        if (matches.Count == 0) return null;

        // 必须出现关键词才认：否则"回复10086"这类号码会被误当成验证码高亮出来。
        var keywordAt = NearestKeywordIndex(body);
        if (keywordAt < 0) return null;

        Match? best = null;
        var bestDistance = int.MaxValue;
        foreach (Match match in matches)
        {
            var distance = Math.Abs(match.Index - keywordAt);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = match;
            }
        }
        return best?.Value;
    }

    private static int NearestKeywordIndex(string body)
    {
        var best = -1;
        foreach (var word in Keywords)
        {
            var at = body.IndexOf(word, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }
}
