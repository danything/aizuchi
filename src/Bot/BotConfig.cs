using SlackClaudeBot.Claude;

namespace SlackClaudeBot.Bot;

public sealed record BotConfig(
    string SlackBotToken,
    string SlackAppToken,
    ClaudeOptions Claude,
    int MaxHistory)
{
    public const string DefaultSystemPrompt = """
        You are an assistant living in a Slack workspace, replying inside threads and DMs.
        Reply in the language the user writes in (Japanese if they write Japanese).
        Formatting: Slack renders a limited Markdown. Prefer short paragraphs, bullet lists, *bold*,
        inline `code`, and fenced code blocks. Do not use Markdown tables or headings deeper than one level.
        Be concise; Slack messages are read on the go.
        """;

    /// <summary>環境変数から組み立てる。必須が欠けていれば理由を列挙した例外</summary>
    public static BotConfig FromEnvironment(Func<string, string?> env)
    {
        var missing = new List<string>();
        string Require(string name)
        {
            var v = env(name);
            if (string.IsNullOrWhiteSpace(v)) missing.Add(name);
            return v ?? "";
        }

        var bot = Require("SLACK_BOT_TOKEN");
        var app = Require("SLACK_APP_TOKEN");
        var key = Require("ANTHROPIC_API_KEY");
        if (missing.Count > 0)
            throw new InvalidOperationException($"環境変数が未設定です: {string.Join(", ", missing)}");

        var extra = env("CLAUDE_SYSTEM_PROMPT");
        var system = string.IsNullOrWhiteSpace(extra) ? DefaultSystemPrompt : DefaultSystemPrompt + "\n\n" + extra.Trim();
        var effort = env("CLAUDE_EFFORT");
        var fallbacks = env("CLAUDE_FALLBACKS");

        return new BotConfig(
            bot, app,
            new ClaudeOptions(
                ApiKey: key,
                Model: env("CLAUDE_MODEL") is { Length: > 0 } m ? m : "claude-opus-5",
                MaxTokens: ParseInt(env("CLAUDE_MAX_TOKENS"), 16_000, "CLAUDE_MAX_TOKENS"),
                Effort: string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
                SystemPrompt: system,
                Fallbacks: !string.Equals(fallbacks?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
                BaseUrl: env("ANTHROPIC_BASE_URL") is { Length: > 0 } u ? u : "https://api.anthropic.com"),
            MaxHistory: ParseInt(env("BOT_MAX_HISTORY"), 50, "BOT_MAX_HISTORY"));
    }

    private static int ParseInt(string? raw, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v) && v > 0
            ? v
            : throw new InvalidOperationException($"{name} は正の整数で指定してください: {raw}");
    }
}
