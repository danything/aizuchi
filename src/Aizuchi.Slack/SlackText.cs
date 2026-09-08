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
    /// Slack の上限を超えないよう改行位置で分割する。上限は文字数で数える。
    ///
    /// chat.postMessage は日本語 4,000 文字(= 12,000 バイト)を通すので、判定はバイト数ではない。
    /// ただし chat.update は 3,800 文字の日本語を msg_too_long で弾いたので、
    /// ドキュメントの「4,000 文字」は当てにせず 2,800 に抑える。
    /// </summary>
    public static List<string> Split(string text, int max = 2_800)
    {
        var parts = new List<string>();
        var rest = text.AsSpan();
        while (rest.Length > max)
        {
            var cut = CutIndex(rest, max);
            parts.Add(rest[..cut].TrimEnd('\n').ToString());
            rest = rest[cut..].TrimStart('\n');
        }
        parts.Add(rest.ToString());
        return parts;
    }

    /// <summary>max に収まる切り位置。改行があればそこで、無ければ文字の境界で切る</summary>
    private static int CutIndex(ReadOnlySpan<char> text, int max)
    {
        var fits = Math.Min(max, text.Length);
        // 絵文字などのサロゲートペアは割らない
        if (fits < text.Length && char.IsLowSurrogate(text[fits])) fits--;
        var newline = text[..fits].LastIndexOf('\n');
        // 改行が手前すぎるなら諦めて入るだけ入れる。1 文字も入らない指定でも進めるよう最低 1 は返す
        return newline >= fits / 2 ? newline : Math.Max(fits, 1);
    }

    private static Regex MentionOf(string botUserId) =>
        new($"<@{Regex.Escape(botUserId)}(\\|[^>]*)?>", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex DoubleSpace();
}
