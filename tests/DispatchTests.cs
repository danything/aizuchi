using Aizuchi.Slack;
using System.Threading.Tasks;

public class DispatchTests
{
    private const string Bot = "UBOT";

    private static SlackEvent Ev(string type = "message", string? text = "hi", string? user = "U1",
        string? channelType = "channel", string? threadTs = null, string? subtype = null, string? botId = null) =>
        new() { Type = type, Text = text, User = user, Channel = "C1", ChannelType = channelType, Ts = "2.0", ThreadTs = threadTs, Subtype = subtype, BotId = botId };

    [Test]
    public async Task DMは常に返す() => await Assert.That(Dispatch.Decide(Ev(channelType: "im"), Bot)).IsEqualTo(Decision.Reply);

    [Test]
    public async Task メンションされたら返す()
    {
        await Assert.That(Dispatch.Decide(Ev(type: "app_mention", text: "<@UBOT> hi", channelType: null), Bot)).IsEqualTo(Decision.Reply);
        await Assert.That(Dispatch.Decide(Ev(text: "<@UBOT> hi"), Bot)).IsEqualTo(Decision.Reply);
    }

    [Test]
    public async Task メンション無しのチャンネル発言は無視()
        => await Assert.That(Dispatch.Decide(Ev(), Bot)).IsEqualTo(Decision.Ignore);

    [Test]
    public async Task スレッド内の返信は履歴を見て判定()
        => await Assert.That(Dispatch.Decide(Ev(threadTs: "1.0"), Bot)).IsEqualTo(Decision.ReplyIfBotInThread);

    [Test]
    public async Task スレッド親自身はスレッド返信扱いしない()
        => await Assert.That(Dispatch.Decide(Ev(threadTs: "2.0"), Bot)).IsEqualTo(Decision.Ignore);

    [Test]
    public async Task 自分と他ボットとサブタイプ付きは無視()
    {
        await Assert.That(Dispatch.Decide(Ev(user: Bot, channelType: "im"), Bot)).IsEqualTo(Decision.Ignore);
        await Assert.That(Dispatch.Decide(Ev(botId: "B1", channelType: "im"), Bot)).IsEqualTo(Decision.Ignore);
        await Assert.That(Dispatch.Decide(Ev(subtype: "message_changed", channelType: "im"), Bot)).IsEqualTo(Decision.Ignore);
        await Assert.That(Dispatch.Decide(Ev(text: "  ", channelType: "im"), Bot)).IsEqualTo(Decision.Ignore);
        await Assert.That(Dispatch.Decide(Ev(type: "reaction_added", channelType: "im"), Bot)).IsEqualTo(Decision.Ignore);
    }

    [Test]
    public async Task 重複キーは一度しか通らない()
    {
        var keys = new RecentKeys(2);
        await Assert.That(keys.Add("a")).IsTrue();
        await Assert.That(keys.Add("a")).IsFalse();
        await Assert.That(keys.Add("b")).IsTrue();
        await Assert.That(keys.Add("c")).IsTrue(); // a が追い出される
        await Assert.That(keys.Add("a")).IsTrue();
    }
}