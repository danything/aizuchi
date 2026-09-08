using TUnit.Assertions.Enums;
using Aizuchi.Slack;
using System.Threading.Tasks;

public class SlackTextTests
{
    [Test]
    public async Task メンションを剥がしてエンティティを戻す()
    {
        await Assert.That(SlackText.StripMention("<@U123> a &lt; b を教えて", "U123")).IsEqualTo("a < b を教えて");
        await Assert.That(SlackText.StripMention("こんにちは <@U123|bot>", "U123")).IsEqualTo("こんにちは");
        await Assert.That(SlackText.StripMention("<@U999> へ <@U123>", "U123")).IsEqualTo("<@U999> へ");
    }

    [Test]
    public async Task メンション判定()
    {
        await Assert.That(SlackText.MentionsBot("hey <@U123>", "U123")).IsTrue();
        await Assert.That(SlackText.MentionsBot("<@U123|claude> hi", "U123")).IsTrue();
        await Assert.That(SlackText.MentionsBot("<@U1234>", "U123")).IsFalse();
        await Assert.That(SlackText.MentionsBot(null, "U123")).IsFalse();
    }

    [Test]
    public async Task 分割は改行位置で行われ短ければそのまま()
    {
        await Assert.That(SlackText.Split("short", 10)).IsEquivalentTo(["short"], CollectionOrdering.Matching);
        var parts = SlackText.Split("aaaa\nbbbb\ncccc", 10);
        await Assert.That(parts).IsEquivalentTo(["aaaa\nbbbb", "cccc"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task 既定の分割幅はchat_updateに収まり中身を落とさない()
    {
        // chat.update は 3,800 文字で msg_too_long になった。日本語も英語も 2,800 文字に抑える
        var japanese = string.Concat(Enumerable.Repeat("あ", 10_000));
        var parts = SlackText.Split(japanese);
        await Assert.That(parts.Count).IsGreaterThan(1);
        await Assert.That(parts).All(p => p.Length <= 2_800);
        await Assert.That(string.Concat(parts)).IsEqualTo(japanese);

        await Assert.That(SlackText.Split(new string('x', 20_000))).All(p => p.Length <= 2_800);
    }

    [Test]
    public async Task 絵文字のサロゲートペアを割らない()
    {
        var parts = SlackText.Split(string.Concat(Enumerable.Repeat("🙂", 2_000)), 100);
        await Assert.That(parts).All(p => !p.Any(char.IsLowSurrogate) || p.Length % 2 == 0);
        await Assert.That(string.Concat(parts)).IsEqualTo(string.Concat(Enumerable.Repeat("🙂", 2_000)));
    }

    [Test]
    public async Task 改行が無ければ固定長で切る()
    {
        var parts = SlackText.Split(new string('x', 25), 10);
        await Assert.That(parts.Count).IsEqualTo(3);
        await Assert.That(parts).All(p => p.Length <= 10);
        await Assert.That(parts.Sum(p => p.Length)).IsEqualTo(25);
    }
}