namespace Aizuchi.Core;

/// <summary>コネクタにもプロバイダにも依らない設定</summary>
public sealed record BotOptions(string SystemPrompt, int MaxHistory, TimeSpan UpdateInterval)
{
    public const string DefaultSystemPrompt = """
        You are an assistant living in a team chat, replying inside threads and DMs.
        Reply in the language the user writes in (Japanese if they write Japanese).
        Formatting: the chat client renders a limited Markdown. Prefer short paragraphs, bullet lists, **bold**,
        inline `code`, and fenced code blocks. Avoid Markdown tables and deep heading levels.
        Be concise; chat messages are read on the go.
        """;

    public static BotOptions FromEnvironment(Func<string, string?> env)
    {
        var extra = env("BOT_SYSTEM_PROMPT");
        var system = string.IsNullOrWhiteSpace(extra) ? DefaultSystemPrompt : DefaultSystemPrompt + "\n\n" + extra.Trim();
        return new BotOptions(
            SystemPrompt: system,
            MaxHistory: Env.PositiveInt(env, "BOT_MAX_HISTORY", 50),
            UpdateInterval: TimeSpan.FromMilliseconds(Env.PositiveInt(env, "BOT_UPDATE_INTERVAL_MS", 1500)));
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

    public static int PositiveInt(Func<string, string?> env, string name, int fallback)
    {
        var raw = env(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v) && v > 0
            ? v
            : throw new ConfigException($"{name} は正の整数で指定してください: {raw}");
    }
}

public sealed class ConfigException(string message) : Exception(message);
