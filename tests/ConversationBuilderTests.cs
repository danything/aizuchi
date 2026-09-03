using TUnit.Assertions.Enums;
using Aizuchi.Core;
using Aizuchi.Slack;
using System.Threading.Tasks;

public class ConversationBuilderTests
{
    private const string Bot = "UBOT";
    private const string BotId = "B1";

    private static SlackMessage Msg(string user, string text, string ts, string? subtype = null, string? botId = null) =>
        new() { User = user, Text = text, Ts = ts, Subtype = subtype, BotId = botId };

    private static SlackEvent Trigger(string text, string ts) =>
        new() { Type = "message", User = "U1", Text = text, Ts = ts, Channel = "C1" };

    [Test]
    public async Task 自分の発言はassistant_他はuserになりメンションは剥がれる()
    {
        var history = new[]
        {
            Msg("U1", "<@UBOT> こんにちは", "1"),
            Msg(Bot, "こんにちは!", "2", botId: BotId),
            Msg("U1", "調子は?", "3"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("調子は?", "3"), 50);
        await Assert.That(result).IsEquivalentTo(new ChatMessage[] { new("user", "こんにちは"), new("assistant", "こんにちは!"), new("user", "調子は?") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task 発火元が履歴に無ければ末尾に足す()
    {
        var history = new[] { Msg("U1", "a", "1"), Msg(Bot, "b", "2") };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("<@UBOT> c", "3"), 50);
        await Assert.That(result[^1].Content).IsEqualTo("c");
        await Assert.That(result[^1].Role).IsEqualTo("user");
    }

    [Test]
    public async Task 先頭のassistantとシステム系サブタイプと仮メッセージは捨てる()
    {
        var history = new[]
        {
            Msg(Bot, "先に喋ってた", "0"),
            Msg("U2", "joined", "1", subtype: "channel_join"),
            Msg("U1", "質問", "2"),
            Msg(Bot, ConversationBuilder.Placeholder, "3"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("質問", "2"), 50);
        await Assert.That(result).IsEquivalentTo(new ChatMessage[] { new("user", "質問") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task 同じロールの連続は畳まれる()
    {
        var history = new[] { Msg("U1", "a", "1"), Msg("U2", "b", "2"), Msg(Bot, "c", "3"), Msg("U1", "d", "4") };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("d", "4"), 50);
        await Assert.That(result).IsEquivalentTo(new ChatMessage[] { new("user", "a\n\nb"), new("assistant", "c"), new("user", "d") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task 上限を超えたら古い方から切りuser始まりを保つ()
    {
        var history = new[]
        {
            Msg("U1", "q1", "1"), Msg(Bot, "a1", "2"),
            Msg("U1", "q2", "3"), Msg(Bot, "a2", "4"),
            Msg("U1", "q3", "5"),
        };
        var result = ConversationBuilder.Build(history, Bot, BotId, Trigger("q3", "5"), 2);
        await Assert.That(result).IsEquivalentTo(new ChatMessage[] { new("user", "q3") }, CollectionOrdering.Matching);
    }
}