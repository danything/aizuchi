using System.Text.RegularExpressions;

namespace Aizuchi.Core;

/// <summary>
/// 外から来た文字列をログに書く前に通す。改行などの制御文字があると、ログの行を偽って足せてしまうので、
/// 制御文字と行・段落の区切り(U+2028、U+2029)を `?` に置き換える
/// </summary>
public static partial class LogText
{
    [GeneratedRegex(@"[\p{Cc}\u2028\u2029]")]
    private static partial Regex Unsafe();

    public static string Clean(string? text) => text is null ? "" : Unsafe().Replace(text, "?");
}
