namespace Aizuchi.Core;

/// <summary>コネクタにもプロバイダにも依らない設定</summary>
/// <param name="MemoryDir">記憶ファイルの置き場。null なら記憶機能なし</param>
/// <param name="MemoryMaxChars">1 スコープあたりの上限文字数</param>
/// <param name="ChannelContext">スレッド返信のとき参考に渡すチャンネル直近メッセージ数。0 で無効</param>
public sealed record BotOptions(
    string SystemPrompt,
    int MaxHistory,
    TimeSpan UpdateInterval,
    string? MemoryDir,
    int MemoryMaxChars,
    int ChannelContext)
{
    public const string DefaultSystemPrompt = """
        You are an assistant living in a team chat, replying inside threads and DMs.
        Reply in the language the user writes in (Japanese if they write Japanese).
        Formatting: the chat client renders a limited Markdown. Prefer short paragraphs, bullet lists, **bold**,
        inline `code`, and fenced code blocks. Avoid Markdown tables and deep heading levels.
        Be concise; chat messages are read on the go.
        In multi-person threads, user messages may be prefixed with the speaker's name in brackets.
        """;

    public static BotOptions FromEnvironment(Func<string, string?> env)
    {
        var extra = env("BOT_SYSTEM_PROMPT");
        var system = string.IsNullOrWhiteSpace(extra) ? DefaultSystemPrompt : DefaultSystemPrompt + "\n\n" + extra.Trim();
        var memoryDir = Env.Or(env, "BOT_MEMORY_DIR", "data/memory");
        return new BotOptions(
            SystemPrompt: system,
            MaxHistory: Env.PositiveInt(env, "BOT_MAX_HISTORY", 50),
            UpdateInterval: TimeSpan.FromMilliseconds(Env.PositiveInt(env, "BOT_UPDATE_INTERVAL_MS", 1500)),
            MemoryDir: string.Equals(memoryDir, "off", StringComparison.OrdinalIgnoreCase) ? null : memoryDir,
            MemoryMaxChars: Env.PositiveInt(env, "BOT_MEMORY_MAX_CHARS", 8000),
            ChannelContext: Env.NonNegativeInt(env, "BOT_CHANNEL_CONTEXT", 20));
    }
}

/// <summary>環境変数の読み取り補助。足りない・壊れているときは理由を日本語で投げる</summary>
public static class Env
{
    public static string Required(Func<string, string?> env, string name) =>
        env(name) is { } v && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ConfigException($"環境変数 {name} が未設定です");

    public static string Or(Func<string, string?> env, string name, string fallback) =>
        env(name) is { Length: > 0 } v ? v : fallback;

    public static string? Optional(Func<string, string?> env, string name) =>
        env(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public static int PositiveInt(Func<string, string?> env, string name, int fallback) =>
        Parse(env, name, fallback, v => v > 0, "正の整数");

    public static int NonNegativeInt(Func<string, string?> env, string name, int fallback) =>
        Parse(env, name, fallback, v => v >= 0, "0 以上の整数");

    private static int Parse(Func<string, string?> env, string name, int fallback, Func<int, bool> ok, string kind)
    {
        var raw = env(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v) && ok(v)
            ? v
            : throw new ConfigException($"{name} は{kind}で指定してください: {raw}");
    }
}

public sealed class ConfigException(string message) : Exception(message);
