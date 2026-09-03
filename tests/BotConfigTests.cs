using SlackClaudeBot.Bot;

public class BotConfigTests
{
    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;

    [Fact]
    public void 必須が欠けると全部列挙して失敗()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BotConfig.FromEnvironment(Env(("SLACK_BOT_TOKEN", "xoxb"))));
        Assert.Contains("SLACK_APP_TOKEN", ex.Message);
        Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
        Assert.DoesNotContain("SLACK_BOT_TOKEN", ex.Message);
    }

    [Fact]
    public void 既定値()
    {
        var c = BotConfig.FromEnvironment(Env(("SLACK_BOT_TOKEN", "b"), ("SLACK_APP_TOKEN", "a"), ("ANTHROPIC_API_KEY", "k")));
        Assert.Equal("claude-opus-5", c.Claude.Model);
        Assert.Equal(16_000, c.Claude.MaxTokens);
        Assert.Null(c.Claude.Effort);
        Assert.True(c.Claude.Fallbacks);
        Assert.Equal(BotConfig.DefaultSystemPrompt, c.Claude.SystemPrompt);
        Assert.Equal(50, c.MaxHistory);
    }

    [Fact]
    public void 上書きと追加プロンプト()
    {
        var c = BotConfig.FromEnvironment(Env(
            ("SLACK_BOT_TOKEN", "b"), ("SLACK_APP_TOKEN", "a"), ("ANTHROPIC_API_KEY", "k"),
            ("CLAUDE_MODEL", "claude-sonnet-5"), ("CLAUDE_EFFORT", "low"), ("CLAUDE_FALLBACKS", "off"),
            ("CLAUDE_SYSTEM_PROMPT", "常に関西弁で。"), ("CLAUDE_MAX_TOKENS", "4096"), ("BOT_MAX_HISTORY", "10")));
        Assert.Equal("claude-sonnet-5", c.Claude.Model);
        Assert.Equal("low", c.Claude.Effort);
        Assert.False(c.Claude.Fallbacks);
        Assert.EndsWith("\n\n常に関西弁で。", c.Claude.SystemPrompt);
        Assert.Equal(4096, c.Claude.MaxTokens);
        Assert.Equal(10, c.MaxHistory);
    }

    [Fact]
    public void 数値が壊れていたら失敗()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BotConfig.FromEnvironment(Env(
            ("SLACK_BOT_TOKEN", "b"), ("SLACK_APP_TOKEN", "a"), ("ANTHROPIC_API_KEY", "k"), ("CLAUDE_MAX_TOKENS", "たくさん"))));
        Assert.Contains("CLAUDE_MAX_TOKENS", ex.Message);
    }
}
