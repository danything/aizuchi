using Aizuchi.Slack;
using System.Threading.Tasks;

public class DispatchTests
{
    private const string Bot = "UBOT";

    private static SlackEvent Ev(string type = "message", string? text = "hi", string? user = "U1",
        string? channelType = "channel", string? threadTs = null, string? subtype = null, string? botId = null) =>
        new() { Type = type, Text = text, User = user, Channel = "C1", ChannelType = channelType, Ts = "2.0", ThreadTs = threadTs, Subtype = subtype, BotId = botId };

    /// <summary>既定はスレッド追従あり。切ったときの違いを見たいテストだけ false を渡す</summary>
    private static Decision Decide(SlackEvent ev, bool threadFollowUp = true) => Dispatch.Decide(ev, Bot, threadFollowUp);

    [Test]
    public async Task DMは常に返す() => await Assert.That(Decide(Ev(channelType: "im"))).IsEqualTo(Decision.Reply);

    [Test]
    public async Task メンションされたら返す()
    {
        await Assert.That(Decide(Ev(type: "app_mention", text: "<@UBOT> hi", channelType: null))).IsEqualTo(Decision.Reply);
        await Assert.That(Decide(Ev(text: "<@UBOT> hi"))).IsEqualTo(Decision.Reply);
    }

    [Test]
    public async Task メンション無しのチャンネル発言は無視()
        => await Assert.That(Decide(Ev())).IsEqualTo(Decision.Ignore);

    [Test]
    public async Task スレッド内の返信は親を見て判定()
        => await Assert.That(Decide(Ev(threadTs: "1.0"))).IsEqualTo(Decision.ReplyIfOwnThread);

    [Test]
    public async Task 親がボットを呼んだスレッドだけ続きに返す()
    {
        static SlackMessage M(string ts, string text) => new() { Ts = ts, Text = text };
        // @aizuchi で始まったスレッド。人同士の続きにも返す
        List<SlackMessage> own = [M("1.0", "<@UBOT> これ調べて"), M("1.5", "ありがとう")];
        await Assert.That(Dispatch.StartedByMention(own, "1.0", Bot)).IsTrue();
        // 人同士のスレッドに途中から呼ばれただけ。親にメンションが無いので追従しない
        List<SlackMessage> joined = [M("1.0", "FAX の件どうする?"), M("1.5", "<@UBOT> 調べて")];
        await Assert.That(Dispatch.StartedByMention(joined, "1.0", Bot)).IsFalse();
        // 親が取れなくても落ちない
        await Assert.That(Dispatch.StartedByMention([], "1.0", Bot)).IsFalse();
        // 並び順が崩れていても ts で親を引く
        await Assert.That(Dispatch.StartedByMention([.. own.AsEnumerable().Reverse()], "1.0", Bot)).IsTrue();
    }

    [Test]
    public async Task 追従を切るとスレッドの続きは無視()
    {
        await Assert.That(Decide(Ev(threadTs: "1.0"), threadFollowUp: false)).IsEqualTo(Decision.Ignore);
        // 呼ばれれば追従が切れていても返す
        await Assert.That(Decide(Ev(threadTs: "1.0", text: "<@UBOT> hi"), threadFollowUp: false)).IsEqualTo(Decision.Reply);
        await Assert.That(Decide(Ev(threadTs: "1.0", channelType: "im"), threadFollowUp: false)).IsEqualTo(Decision.Reply);
    }

    [Test]
    public async Task スレッド親自身はスレッド返信扱いしない()
        => await Assert.That(Decide(Ev(threadTs: "2.0"))).IsEqualTo(Decision.Ignore);

    [Test]
    public async Task 自分と他ボットとサブタイプ付きは無視()
    {
        await Assert.That(Decide(Ev(user: Bot, channelType: "im"))).IsEqualTo(Decision.Ignore);
        await Assert.That(Decide(Ev(botId: "B1", channelType: "im"))).IsEqualTo(Decision.Ignore);
        await Assert.That(Decide(Ev(subtype: "message_changed", channelType: "im"))).IsEqualTo(Decision.Ignore);
        await Assert.That(Decide(Ev(text: "  ", channelType: "im"))).IsEqualTo(Decision.Ignore);
        await Assert.That(Decide(Ev(type: "reaction_added", channelType: "im"))).IsEqualTo(Decision.Ignore);
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