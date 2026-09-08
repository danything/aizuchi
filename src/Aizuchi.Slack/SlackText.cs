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
    /// 分割の既定幅(UTF-8 バイト)。chat.update の上限 4,000 バイトに、
    /// 途中経過に付けるカーソル(" ▍" = 4 バイト)の分の余裕を持たせた値。
    /// 日本語なら約 1,266 文字、英語なら 3,800 文字。
    /// </summary>
    public const int DefaultMaxBytes = 3_800;

    /// <summary>
    /// Slack の上限を超えないよう改行位置で分割する。上限は UTF-8 のバイト数で数える。
    ///
    /// chat.update のドキュメントは「text は 4,000 characters まで」と書いているが、
    /// 実測すると characters ではなく bytes だった(二分探索で通る最大を測った結果):
    ///   ASCII    4,000 文字 = 4,000 バイト → 通る
    ///   日本語   1,333 文字 = 3,999 バイト → ここが天井。1,334 文字で msg_too_long
    /// 文字数で数えると日本語だけ 3 倍の量を送ってしまうので、バイトで数える。
    /// なお chat.postMessage はこれよりずっと緩く、日本語 4,000 文字でも通る。
    /// </summary>
    public static List<string> Split(string text, int maxBytes = DefaultMaxBytes)
    {
        var parts = new List<string>();
        var rest = text.AsSpan();
        while (Encoding.UTF8.GetByteCount(rest) > maxBytes)
        {
            var cut = CutIndex(rest, maxBytes);
            parts.Add(rest[..cut].TrimEnd('\n').ToString());
            rest = rest[cut..].TrimStart('\n');
        }
        parts.Add(rest.ToString());
        return parts;
    }

    /// <summary>maxBytes に収まる切り位置。改行があればそこで、無ければ文字の境界で切る</summary>
    private static int CutIndex(ReadOnlySpan<char> text, int maxBytes)
    {
        int bytes = 0, fits = 0, newline = -1;
        while (fits < text.Length)
        {
            // 絵文字などのサロゲートペアは割らない
            var width = char.IsHighSurrogate(text[fits]) && fits + 1 < text.Length ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(text.Slice(fits, width));
            if (bytes > maxBytes) break;
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
