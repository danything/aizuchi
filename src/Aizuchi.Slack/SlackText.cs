using System.Text;
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
    /// Slack の上限を超えないよう改行位置で分割する。バイト数と文字数の両方で抑える。
    ///
    /// chat.update のドキュメントは text 4,000 文字と書いているが、3,800 文字の日本語は
    /// msg_too_long で弾かれた。辻褄の合う説明が 2 つあり、どちらが正しいか確かめられていない:
    /// (A) 実際はバイト数で見ている(3,800 文字の日本語 = 約 11,400 バイト)
    /// (B) 文字数だが上限は 4,000 ではなく 3,000(blocks 側の上限と同じ)
    /// 日本語だけ落ちるのが (A)、英語だけ落ちるのが (B) なので、両方に収まる幅にしておく。
    /// </summary>
    public static List<string> Split(string text, int maxBytes = 3_500, int maxChars = 2_800)
    {
        var parts = new List<string>();
        var rest = text.AsSpan();
        while (Encoding.UTF8.GetByteCount(rest) > maxBytes || rest.Length > maxChars)
        {
            var cut = CutIndex(rest, maxBytes, maxChars);
            parts.Add(rest[..cut].TrimEnd('\n').ToString());
            rest = rest[cut..].TrimStart('\n');
        }
        parts.Add(rest.ToString());
        return parts;
    }

    /// <summary>どちらの上限にも収まる切り位置。改行があればそこで、無ければ文字の境界で切る</summary>
    private static int CutIndex(ReadOnlySpan<char> text, int maxBytes, int maxChars)
    {
        int bytes = 0, fits = 0, newline = -1;
        while (fits < text.Length)
        {
            // サロゲートペアは割らない
            var width = char.IsHighSurrogate(text[fits]) && fits + 1 < text.Length ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(text.Slice(fits, width));
            if (bytes > maxBytes || fits + width > maxChars) break;
            fits += width;
            if (text[fits - 1] == '\n') newline = fits;
        }
        // 改行が手前すぎるなら諦めて入るだけ入れる。1 文字も入らない指定でも進めるよう最低 1 は返す
        return newline >= fits / 2 && newline > 0 ? newline : Math.Max(fits, 1);
    }

    private static Regex MentionOf(string botUserId) =>
        new($"<@{Regex.Escape(botUserId)}(\\|[^>]*)?>", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex DoubleSpace();
}
