using SlackClaudeBot.Bot;
using SlackClaudeBot.Slack;

public class ConversationBuilderTests
{
    private const string Bot = "UBOT";
    private const string BotId = "B1";

    private static SlackMessage Msg(string user, string text, string ts, string? subtype = null, string? botId = null) =>
        new() { User = user, Text = text, Ts = ts, Subtype = subtype, BotId = botId };

    private static SlackEvent Trigger(string text, string ts) =>
        new() { Type = "message", User = "U1", Text = text, Ts = ts, Channel = "C1" };

    [Fact]
    public void 自分の発言はassistant_他はuserになりメンションは剥がれる()
    {
        var history = new[]
        {
            Msg("U1", "<@UBOT> こんにちは", "1"),
            Msg(Bot, "こんにちは!", "2", botId: BotId),
            Msg("U1", "調子は?", "3"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("調子は?", "3"), 50);
        Assert.Equal(
            [new("user", "こんにちは"), new("assistant", "こんにちは!"), new("user", "調子は?")],
            result);
    }

    [Fact]
    public void 発火元が履歴に無ければ末尾に足す()
    {
        var history = new[] { Msg("U1", "a", "1"), Msg(Bot, "b", "2") };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("<@UBOT> c", "3"), 50);
        Assert.Equal("c", result[^1].Content);
        Assert.Equal("user", result[^1].Role);
    }

    [Fact]
    public void 先頭のassistantとシステム系サブタイプと仮メッセージは捨てる()
    {
        var history = new[]
        {
            Msg(Bot, "先に喋ってた", "0"),
            Msg("U2", "joined", "1", subtype: "channel_join"),
            Msg("U1", "質問", "2"),
            Msg(Bot, ConversationBuilder.Placeholder, "3"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("質問", "2"), 50);
        Assert.Equal([new("user", "質問")], result);
    }

    [Fact]
    public void 同じロールの連続は畳まれる()
    {
        var history = new[] { Msg("U1", "a", "1"), Msg("U2", "b", "2"), Msg(Bot, "c", "3"), Msg("U1", "d", "4") };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("d", "4"), 50);
        Assert.Equal([new("user", "a\n\nb"), new("assistant", "c"), new("user", "d")], result);
    }

    [Fact]
    public void 上限を超えたら古い方から切りuser始まりを保つ()
    {
        var history = new[]
        {
            Msg("U1", "q1", "1"), Msg(Bot, "a1", "2"),
            Msg("U1", "q2", "3"), Msg(Bot, "a2", "4"),
            Msg("U1", "q3", "5"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("q3", "5"), 2);
        Assert.Equal([new("user", "q3")], result);
    }
}
