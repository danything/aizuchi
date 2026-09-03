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
public class ConversationContextTests
{
    private const string Bot = "UBOT";

    private static SlackMessage Msg(string user, string text, string ts, string? subtype = null) =>
        new() { User = user, Text = text, Ts = ts, Subtype = subtype };

    [Test]
    public async Task 複数人のスレッドでは発言者名を前置する_一人なら付けない()
    {
        var names = new Dictionary<string, string> { ["U1"] = "田中", ["U2"] = "鈴木" };
        var trigger = new SlackEvent { Type = "message", User = "U2", Text = "どう思う?", Ts = "3", Channel = "C1" };
        var multi = ConversationBuilder.Build(
            [Msg("U1", "<@UBOT> 相談", "1"), Msg(Bot, "はい", "2"), Msg("U2", "どう思う?", "3")],
            Bot, null, trigger, 50, names);
        await Assert.That(multi[0].Content).IsEqualTo("[田中] 相談");
        await Assert.That(multi[2].Content).IsEqualTo("[鈴木] どう思う?");

        var single = ConversationBuilder.Build([Msg("U1", "相談", "1")], Bot, null,
            new SlackEvent { Type = "message", User = "U1", Text = "相談", Ts = "1", Channel = "C1" }, 50, names);
        await Assert.That(single[0].Content).IsEqualTo("相談");
    }

    [Test]
    public async Task チャンネルの直近は先頭のuser発言の前に付く()
    {
        var names = new Dictionary<string, string> { ["U1"] = "田中" };
        var recent = new List<SlackMessage>
        {
            Msg("U1", "今日リリースします", "0.1"),
            Msg("U9", "joined", "0.2", subtype: "channel_join"),
            Msg(Bot, "了解", "0.3"),
            Msg("U1", "親メッセージ", "1"),
        };
        var preamble = ConversationBuilder.ChannelContext(recent, "1", Bot, null, names, 20);
        await Assert.That(preamble).IsEqualTo("(参考: このチャンネルの直近のやりとり。スレッドの外)\n[田中] 今日リリースします\n[aizuchi] 了解");

        var trigger = new SlackEvent { Type = "message", User = "U1", Text = "質問", Ts = "2", Channel = "C1" };
        var built = ConversationBuilder.Build([Msg("U1", "親メッセージ", "1"), Msg("U1", "質問", "2")], Bot, null, trigger, 50, names, preamble);
        await Assert.That(built.Count).IsEqualTo(1);
        await Assert.That(built[0].Content).StartsWith("(参考: このチャンネルの直近のやりとり");
        await Assert.That(built[0].Content).EndsWith("---\n\n親メッセージ\n\n質問");

        await Assert.That(ConversationBuilder.ChannelContext([], null, Bot, null, names, 20)).IsNull();
    }
}
