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
    public async Task 改行が無ければ固定長で切る()
    {
        var parts = SlackText.Split(new string('x', 25), 10);
        await Assert.That(parts.Count).IsEqualTo(3);
        await Assert.That(parts).All(p => p.Length <= 10);
        await Assert.That(parts.Sum(p => p.Length)).IsEqualTo(25);
    }
}