using Aizuchi.Claude;
using Aizuchi.Core;
using Aizuchi.Slack;

public class OptionsTests
{
    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;

    [Fact]
    public void 必須が欠けると名前を挙げて失敗()
    {
        var ex = Assert.Throws<ConfigException>(() => SlackOptions.FromEnvironment(Env(("SLACK_BOT_TOKEN", "xoxb"))));
        Assert.Contains("SLACK_APP_TOKEN", ex.Message);
        Assert.Contains("ANTHROPIC_API_KEY", Assert.Throws<ConfigException>(() => ClaudeOptions.FromEnvironment(Env())).Message);
    }

    [Fact]
    public void Claudeの既定値()
    {
        var c = ClaudeOptions.FromEnvironment(Env(("ANTHROPIC_API_KEY", "k")));
        Assert.Equal("claude-opus-5", c.Model);
        Assert.Equal(16_000, c.MaxTokens);
        Assert.Null(c.Effort);
        Assert.True(c.Fallbacks);
        Assert.Equal("https://api.anthropic.com", c.BaseUrl);
    }

    [Fact]
    public void Claudeの上書き()
    {
        var c = ClaudeOptions.FromEnvironment(Env(("ANTHROPIC_API_KEY", "k"),
            ("CLAUDE_MODEL", "claude-sonnet-5"), ("CLAUDE_EFFORT", "low"), ("CLAUDE_FALLBACKS", "off"), ("CLAUDE_MAX_TOKENS", "4096")));
        Assert.Equal("claude-sonnet-5", c.Model);
        Assert.Equal("low", c.Effort);
        Assert.False(c.Fallbacks);
        Assert.Equal(4096, c.MaxTokens);
    }

    [Fact]
    public void Botの既定値と追加プロンプト()
    {
        var b = BotOptions.FromEnvironment(Env());
        Assert.Equal(BotOptions.DefaultSystemPrompt, b.SystemPrompt);
        Assert.Equal(50, b.MaxHistory);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), b.UpdateInterval);

        var custom = BotOptions.FromEnvironment(Env(("BOT_SYSTEM_PROMPT", "常に関西弁で。"), ("BOT_MAX_HISTORY", "10"), ("BOT_UPDATE_INTERVAL_MS", "500")));
        Assert.EndsWith("\n\n常に関西弁で。", custom.SystemPrompt);
        Assert.Equal(10, custom.MaxHistory);
        Assert.Equal(TimeSpan.FromMilliseconds(500), custom.UpdateInterval);
    }

    [Fact]
    public void 数値が壊れていたら失敗()
    {
        var ex = Assert.Throws<ConfigException>(() => BotOptions.FromEnvironment(Env(("BOT_MAX_HISTORY", "たくさん"))));
        Assert.Contains("BOT_MAX_HISTORY", ex.Message);
    }
}
