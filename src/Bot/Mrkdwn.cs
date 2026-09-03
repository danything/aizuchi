using System.Text.RegularExpressions;

namespace SlackClaudeBot.Bot;

/// <summary>
/// Claude が返す Markdown を Slack の mrkdwn に寄せる。完全変換は目指さず、
/// 崩れて読めなくなる要素(太字・見出し・リンク・表・箸条書き・エスケープ)だけ直す。
/// </summary>
public static partial class Mrkdwn
{
    private const char StashOpen = '';
    private const char StashClose = '';
    private const char BoldMark = '';

    public static string FromMarkdown(string markdown)
    {
        var stash = new List<string>();
        string Stash(string s)
        {
            stash.Add(s);
            return $"{StashOpen}{stash.Count - 1}{StashClose}";
        }

        var text = markdown.Replace("\r\n", "\n");

        // 1. 他の変換を掛けたくない部分を退避する(中身は Slack 向けにエスケープ)
        text = FencedCode().Replace(text, m => Stash("```\n" + Escape(m.Groups[1].Value.TrimEnd('\n')) + "\n```"));
        text = Table().Replace(text, m => Stash(TableToCodeBlock(m.Value)));
        text = InlineCode().Replace(text, m => Stash("`" + Escape(m.Groups[1].Value) + "`"));

        // 2. 本文のエスケープ。Slack の mrkdwn は & < > がエンティティ必須
        text = Escape(text);

        // 3. 装飾の書き換え
        text = BlockQuote().Replace(text, "> ");
        text = Heading().Replace(text, $"{BoldMark}$1{BoldMark}");
        text = BoldStars().Replace(text, $"{BoldMark}$1{BoldMark}");
        text = BoldUnderscores().Replace(text, $"{BoldMark}$1{BoldMark}");
        text = ItalicStar().Replace(text, "_$1_");
        text = text.Replace(BoldMark, '*');
        text = Strike().Replace(text, "~$1~");
        text = Link().Replace(text, "<$2|$1>");
        text = Bullet().Replace(text, "$1• ");
        text = HorizontalRule().Replace(text, "──────────");

        // 4. 退避を戻す
        text = StashRef().Replace(text, m => stash[int.Parse(m.Groups[1].Value)]);
        return text.Trim();
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Markdown の表は Slack で崩れるので、区切り行を捨てて等幅ブロックにする</summary>
    private static string TableToCodeBlock(string table)
    {
        var rows = table.Trim().Split('\n')
            .Where(l => !TableSeparator().IsMatch(l))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        if (rows.Count == 0) return "";
        var cols = rows.Max(r => r.Length);
        var widths = new int[cols];
        foreach (var r in rows)
            for (var i = 0; i < r.Length; i++)
                widths[i] = Math.Max(widths[i], Width(r[i]));
        var lines = rows.Select(r => string.Join("  ", r.Select((c, i) => c + new string(' ', widths[i] - Width(c)))).TrimEnd());
        return "```\n" + Escape(string.Join("\n", lines)) + "\n```";
    }

    /// <summary>全角はおおむね 2 桁幅として桁揃えする</summary>
    private static int Width(string s) => s.Sum(c => c > 0x2E7F ? 2 : 1);

    [GeneratedRegex(@"```[^\n]*\n?([\s\S]*?)```")]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"(?:^[ \t]*\|.*\|[ \t]*(?:\n|$)){2,}", RegexOptions.Multiline)]
    private static partial Regex Table();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^&gt; ?", RegexOptions.Multiline)]
    private static partial Regex BlockQuote();

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex BoldStars();

    [GeneratedRegex(@"(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)")]
    private static partial Regex BoldUnderscores();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex ItalicStar();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"\[([^\]\n]+)\]\((\S+?)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^([ \t]*)[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRule();

    [GeneratedRegex("(\\d+)")]
    private static partial Regex StashRef();
}
