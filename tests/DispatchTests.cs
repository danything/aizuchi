using Aizuchi.Slack;

public class DispatchTests
{
    private const string Bot = "UBOT";

    private static SlackEvent Ev(string type = "message", string? text = "hi", string? user = "U1",
        string? channelType = "channel", string? threadTs = null, string? subtype = null, string? botId = null) =>
        new() { Type = type, Text = text, User = user, Channel = "C1", ChannelType = channelType, Ts = "2.0", ThreadTs = threadTs, Subtype = subtype, BotId = botId };

    [Fact]
    public void DMは常に返す() => Assert.Equal(Decision.Reply, Dispatch.Decide(Ev(channelType: "im"), Bot));

    [Fact]
    public void メンションされたら返す()
    {
        Assert.Equal(Decision.Reply, Dispatch.Decide(Ev(type: "app_mention", text: "<@UBOT> hi", channelType: null), Bot));
        Assert.Equal(Decision.Reply, Dispatch.Decide(Ev(text: "<@UBOT> hi"), Bot));
    }

    [Fact]
    public void メンション無しのチャンネル発言は無視()
        => Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(), Bot));

    [Fact]
    public void スレッド内の返信は履歴を見て判定()
        => Assert.Equal(Decision.ReplyIfBotInThread, Dispatch.Decide(Ev(threadTs: "1.0"), Bot));

    [Fact]
    public void スレッド親自身はスレッド返信扱いしない()
        => Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(threadTs: "2.0"), Bot));

    [Fact]
    public void 自分と他ボットとサブタイプ付きは無視()
    {
        Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(user: Bot, channelType: "im"), Bot));
        Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(botId: "B1", channelType: "im"), Bot));
        Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(subtype: "message_changed", channelType: "im"), Bot));
        Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(text: "  ", channelType: "im"), Bot));
        Assert.Equal(Decision.Ignore, Dispatch.Decide(Ev(type: "reaction_added", channelType: "im"), Bot));
    }

    [Fact]
    public void 重複キーは一度しか通らない()
    {
        var keys = new RecentKeys(2);
        Assert.True(keys.Add("a"));
        Assert.False(keys.Add("a"));
        Assert.True(keys.Add("b"));
        Assert.True(keys.Add("c")); // a が追い出される
        Assert.True(keys.Add("a"));
    }
}
