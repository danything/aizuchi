using Aizuchi.Claude;
using Aizuchi.Core;
using Aizuchi.Slack;
using System.Threading.Tasks;

public class OptionsTests
{
    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;

    [Test]
    public async Task 必須が欠けると名前を挙げて失敗()
    {
        var ex = Assert.Throws<ConfigException>(() => SlackOptions.FromEnvironment(Env(("SLACK_BOT_TOKEN", "xoxb"))));
        await Assert.That(ex.Message).Contains("SLACK_APP_TOKEN");
        await Assert.That(Assert.Throws<ConfigException>(() => ClaudeOptions.FromEnvironment(Env())).Message).Contains("ANTHROPIC_API_KEY");
    }

    [Test]
    public async Task スレッド追従は既定で入っていてoffで切れる()
    {
        var tokens = new[] { ("SLACK_BOT_TOKEN", "xoxb"), ("SLACK_APP_TOKEN", "xapp") };
        await Assert.That(SlackOptions.FromEnvironment(Env(tokens)).ThreadFollowUp).IsTrue();
        await Assert.That(SlackOptions.FromEnvironment(Env([.. tokens, ("SLACK_THREAD_FOLLOWUP", "off")])).ThreadFollowUp).IsFalse();
        await Assert.That(SlackOptions.FromEnvironment(Env([.. tokens, ("SLACK_THREAD_FOLLOWUP", "on")])).ThreadFollowUp).IsTrue();
    }

    [Test]
    public async Task Claudeの既定値()
    {
        var c = ClaudeOptions.FromEnvironment(Env(("ANTHROPIC_API_KEY", "k")));
        await Assert.That(c.Model).IsEqualTo("claude-opus-5");
        await Assert.That(c.MaxTokens).IsEqualTo(16_000);
        await Assert.That(c.Effort).IsNull();
        await Assert.That(c.Fallbacks).IsTrue();
        await Assert.That(c.BaseUrl).IsEqualTo("https://api.anthropic.com");
    }

    [Test]
    public async Task Claudeの上書き()
    {
        var c = ClaudeOptions.FromEnvironment(Env(("ANTHROPIC_API_KEY", "k"),
            ("CLAUDE_MODEL", "claude-sonnet-5"), ("CLAUDE_EFFORT", "low"), ("CLAUDE_FALLBACKS", "off"), ("CLAUDE_MAX_TOKENS", "4096")));
        await Assert.That(c.Model).IsEqualTo("claude-sonnet-5");
        await Assert.That(c.Effort).IsEqualTo("low");
        await Assert.That(c.Fallbacks).IsFalse();
        await Assert.That(c.MaxTokens).IsEqualTo(4096);
    }

    [Test]
    public async Task Botの既定値と追加プロンプト()
    {
        var b = BotOptions.FromEnvironment(Env());
        await Assert.That(b.SystemPrompt).IsEqualTo(BotOptions.DefaultSystemPrompt);
        await Assert.That(b.MaxHistory).IsEqualTo(50);
        await Assert.That(b.UpdateInterval).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(b.MemoryDir).IsEqualTo("data/memory");
        await Assert.That(b.MemoryMaxChars).IsEqualTo(8000);
        await Assert.That(b.ChannelContext).IsEqualTo(20);
        await Assert.That(BotOptions.FromEnvironment(Env(("BOT_MEMORY_DIR", "off"), ("BOT_CHANNEL_CONTEXT", "0"))).MemoryDir).IsNull();
        await Assert.That(BotOptions.FromEnvironment(Env(("BOT_CHANNEL_CONTEXT", "0"))).ChannelContext).IsEqualTo(0);

        var custom = BotOptions.FromEnvironment(Env(("BOT_SYSTEM_PROMPT", "常に関西弁で。"), ("BOT_MAX_HISTORY", "10"), ("BOT_UPDATE_INTERVAL_MS", "500")));
        await Assert.That(custom.SystemPrompt).EndsWith("\n\n常に関西弁で。");
        await Assert.That(custom.MaxHistory).IsEqualTo(10);
        await Assert.That(custom.UpdateInterval).IsEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task 数値が壊れていたら失敗()
    {
        var ex = Assert.Throws<ConfigException>(() => BotOptions.FromEnvironment(Env(("BOT_MAX_HISTORY", "たくさん"))));
        await Assert.That(ex.Message).Contains("BOT_MAX_HISTORY");
    }
}