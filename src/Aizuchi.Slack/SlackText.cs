using System.Text.RegularExpressions;

namespace Aizuchi.Slack;

/// <summary>Slack のメッセージ本文まわりの純粋関数</summary>
public static partial class SlackText
{
    /// <summary>Slack は &amp; &lt; &gt; をエンティティにして渡してくるので Claude に渡す前に戻す</summary>
    public static string Decode(string text) =>
        text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    public static bool MentionsBot(string? text, string botUserId) =>
        text is not null && MentionOf(botUserId).IsMatch(text);

    /// <summary>ボット宛のメンションを取り除いてエンティティも戻す</summary>
    public static string StripMention(string? text, string botUserId)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var stripped = MentionOf(botUserId).Replace(text, "");
        return Decode(DoubleSpace().Replace(stripped, " ").Trim());
    }

    /// <summary>
    /// Slack の 1 メッセージ上限(40,000 文字)を超えないよう改行位置で分割する。
    /// 読みやすさ優先で既定は 12,000 文字。
    /// </summary>
    public static List<string> Split(string text, int max = 12_000)
    {
        var parts = new List<string>();
        var rest = text;
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf('\n', max - 1);
            if (cut < max / 2) cut = max; // 改行が無い/遠すぎるなら固定長で切る
            parts.Add(rest[..cut].TrimEnd('\n'));
            rest = rest[cut..].TrimStart('\n');
        }
        parts.Add(rest);
        return parts;
    }

    private static Regex MentionOf(string botUserId) =>
        new($"<@{Regex.Escape(botUserId)}(\\|[^>]*)?>", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex DoubleSpace();
}
