using System.Text.RegularExpressions;

namespace Aizuchi.Core;

/// <summary>
/// LLM を通さない手動の記憶操作。
///   memory / 記憶                … 表示
///   memory channel / 記憶 チャンネル … 表示(同上。表示は常に両方出す)
///   memory ```…```               … shared を丸ごと置き換え
///   memory channel ```…```       … channel を丸ごと置き換え
/// </summary>
public static partial class MemoryCommand
{
    public sealed record Command(bool IsChannel, string? Replacement);

    public static bool TryParse(string text, out Command command)
    {
        command = null!;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        var isChannel = m.Groups["scope"].Success;
        var replacement = m.Groups["body"].Success ? m.Groups["body"].Value.Trim() : null;
        command = new Command(isChannel, replacement);
        return true;
    }

    [GeneratedRegex(@"^(?:memory|記憶)(?:[ \t]+(?<scope>channel|チャンネル))?[ \t]*(?:\n?```[^\n]*\n?(?<body>[\s\S]*?)```)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>表示用の Markdown</summary>
    public static string Render(MemorySnapshot memory, int maxChars)
    {
        static string Block(string s) => s.Trim().Length == 0 ? "_(まだ何もありません)_" : "```\n" + s.Trim() + "\n```";
        return $"""
            **共有の記憶** ({memory.Shared.Trim().Length}/{maxChars} 文字)
            {Block(memory.Shared)}

            **このチャンネルの記憶** ({memory.Channel.Trim().Length}/{maxChars} 文字)
            {Block(memory.Channel)}

            _書き換えるときは `memory` (または `memory channel`) に続けて全文をコードブロックで送るか、普通に「覚えて」「忘れて」と頼んでください。_
            """;
    }
}
