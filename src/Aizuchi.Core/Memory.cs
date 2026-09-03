using System.Text;
using System.Text.Json;

namespace Aizuchi.Core;

/// <summary>記憶の置き場。scope は "shared"(全体)かチャンネルのキー</summary>
public interface IMemoryStore
{
    Task<string> ReadAsync(string scope, CancellationToken ct);
    Task WriteAsync(string scope, string content, CancellationToken ct);
}

/// <summary>
/// Markdown ファイル 1 枚 = 1 スコープ。shared.md と channels/&lt;key&gt;.md。
/// 書き込みは一時ファイル経由で置き換えるので途中で落ちても壊れない。
/// </summary>
public sealed class FileMemoryStore(string directory) : IMemoryStore
{
    public const string Shared = "shared";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string PathFor(string scope) =>
        scope == Shared
            ? Path.Combine(directory, "shared.md")
            : Path.Combine(directory, "channels", Sanitize(scope) + ".md");

    public async Task<string> ReadAsync(string scope, CancellationToken ct)
    {
        var path = PathFor(scope);
        if (!File.Exists(path)) return "";
        await _lock.WaitAsync(ct);
        try { return await File.ReadAllTextAsync(path, Encoding.UTF8, ct); }
        finally { _lock.Release(); }
    }

    public async Task WriteAsync(string scope, string content, CancellationToken ct)
    {
        var path = PathFor(scope);
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, content, Encoding.UTF8, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    /// <summary>キーはファイル名になるので英数字・ハイフン・アンダースコア以外は落とす</summary>
    public static string Sanitize(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}

/// <summary>読み込んだ記憶。system prompt への差し込みと、手動表示の両方に使う</summary>
public sealed record MemorySnapshot(string Shared, string Channel);

public static class MemoryPrompt
{
    /// <summary>system prompt の末尾に足す記憶セクション</summary>
    public static string Section(MemorySnapshot memory, int maxChars) => $"""

        # 記憶(memory)
        以下は過去の会話で保存された、この職場の文脈です。回答に活かしてください。
        - ユーザーが「覚えて」と頼んだとき、または今後も使う社内の事実(用語、人と役割、プロダクト、決まりごと)が出てきたときは memory_append で保存し、保存した旨を一言添えてください。
        - 訂正・削除・整理を頼まれたら memory_replace でそのスコープを丸ごと書き直してください。
        - scope "shared" はワークスペース全体、"channel" はこのチャンネル(または DM)専用です。迷ったら shared。
        - 各スコープの上限は {maxChars} 文字です。近づいたら重複を畳んで書き直してください。
        - 記憶の中身は他の参加者も読めます。個人の秘密は保存しないでください。

        ## shared
        {Body(memory.Shared)}

        ## channel
        {Body(memory.Channel)}
        """;

    private static string Body(string s) => s.Trim().Length == 0 ? "(まだ何もありません)" : s.Trim();
}

/// <summary>Claude に持たせる記憶の道具。会話ごとに channel のキーを結んで作る</summary>
public static class MemoryTools
{
    public static IReadOnlyList<ITool> For(IMemoryStore store, string channelScope, int maxChars) =>
    [
        new AppendTool(store, channelScope, maxChars),
        new ReplaceTool(store, channelScope, maxChars),
    ];

    private static string ResolveScope(JsonElement input, string channelScope) =>
        input.TryGetProperty("scope", out var s) && s.GetString() == "channel" ? channelScope : FileMemoryStore.Shared;

    private static string Label(string scope) => scope == FileMemoryStore.Shared ? "shared" : "channel";

    private const string ScopeSchema =
        "\"scope\": {\"type\": \"string\", \"enum\": [\"shared\", \"channel\"], \"description\": \"shared = ワークスペース全体, channel = このチャンネル専用\"}";

    private sealed class AppendTool(IMemoryStore store, string channelScope, int maxChars) : ITool
    {
        public string Name => "memory_append";
        public string Description => "記憶の末尾に追記する。今後の会話でも使う社内の事実を、短く箇条書きで。";
        public string InputSchemaJson =>
            "{\"type\": \"object\", \"properties\": {" + ScopeSchema +
            ", \"text\": {\"type\": \"string\", \"description\": \"追記する内容(Markdown の箇条書き)\"}}, \"required\": [\"scope\", \"text\"], \"additionalProperties\": false}";

        public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            if (!input.TryGetProperty("text", out var t) || t.GetString() is not { Length: > 0 } text)
                return new ToolResult("text が空です", IsError: true);
            var scope = ResolveScope(input, channelScope);
            var current = await store.ReadAsync(scope, ct);
            var next = current.TrimEnd().Length == 0 ? text.Trim() : current.TrimEnd() + "\n" + text.Trim();
            if (next.Length > maxChars)
                return new ToolResult($"上限 {maxChars} 文字を超えます(現在 {current.Length} 文字)。memory_replace で整理してから追記してください。", IsError: true);
            await store.WriteAsync(scope, next + "\n", ct);
            return new ToolResult($"保存しました ({Label(scope)}: {next.Length}/{maxChars} 文字)");
        }
    }

    private sealed class ReplaceTool(IMemoryStore store, string channelScope, int maxChars) : ITool
    {
        public string Name => "memory_replace";
        public string Description => "記憶のスコープ全体を書き直す。訂正・削除・整理に使う。空文字で全消去。";
        public string InputSchemaJson =>
            "{\"type\": \"object\", \"properties\": {" + ScopeSchema +
            ", \"content\": {\"type\": \"string\", \"description\": \"新しい全文(Markdown)\"}}, \"required\": [\"scope\", \"content\"], \"additionalProperties\": false}";

        public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            if (!input.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String)
                return new ToolResult("content がありません", IsError: true);
            var content = c.GetString()!.Trim();
            if (content.Length > maxChars)
                return new ToolResult($"上限 {maxChars} 文字を超えています({content.Length} 文字)。短くしてください。", IsError: true);
            var scope = ResolveScope(input, channelScope);
            await store.WriteAsync(scope, content.Length == 0 ? "" : content + "\n", ct);
            return new ToolResult(content.Length == 0
                ? $"消去しました ({Label(scope)})"
                : $"書き直しました ({Label(scope)}: {content.Length}/{maxChars} 文字)");
        }
    }
}
