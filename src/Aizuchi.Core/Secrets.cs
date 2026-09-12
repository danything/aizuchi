using System.Text.RegularExpressions;

namespace Aizuchi.Core;

/// <summary>
/// 会話に貼られた鍵を LLM に渡す前に伏せる。DM で送られた whsec_ などは Slack の履歴に残り、
/// 次の会話で文脈として読み込まれてしまうため。
/// </summary>
public static partial class Secrets
{
    [GeneratedRegex(@"\b(whsec|xoxb|xoxp|xapp|sk-ant)[-_][A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Redact(string text) =>
        Pattern().Replace(text, m => m.Groups[1].Value + "_…(伏せ)");
}
