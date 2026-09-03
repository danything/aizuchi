using SlackClaudeBot.Bot;

public class SlackTextTests
{
    [Fact]
    public void メンションを剥がしてエンティティを戻す()
    {
        Assert.Equal("a < b を教えて", SlackText.StripMention("<@U123> a &lt; b を教えて", "U123"));
        Assert.Equal("こんにちは", SlackText.StripMention("こんにちは <@U123|bot>", "U123"));
        Assert.Equal("<@U999> へ", SlackText.StripMention("<@U999> へ <@U123>", "U123"));
    }

    [Fact]
    public void メンション判定()
    {
        Assert.True(SlackText.MentionsBot("hey <@U123>", "U123"));
        Assert.True(SlackText.MentionsBot("<@U123|claude> hi", "U123"));
        Assert.False(SlackText.MentionsBot("<@U1234>", "U123"));
        Assert.False(SlackText.MentionsBot(null, "U123"));
    }

    [Fact]
    public void 分割は改行位置で行われ短ければそのまま()
    {
        Assert.Equal(["short"], SlackText.Split("short", 10));
        var parts = SlackText.Split("aaaa\nbbbb\ncccc", 10);
        Assert.Equal(["aaaa\nbbbb", "cccc"], parts);
    }

    [Fact]
    public void 改行が無ければ固定長で切る()
    {
        var parts = SlackText.Split(new string('x', 25), 10);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.Length <= 10));
        Assert.Equal(25, parts.Sum(p => p.Length));
    }
}
