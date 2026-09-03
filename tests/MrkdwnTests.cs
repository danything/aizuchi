using Aizuchi.Slack;

public class MrkdwnTests
{
    [Fact]
    public void 太字は二重アスタリスクから一重に()
    {
        Assert.Equal("これは *重要* です", Mrkdwn.FromMarkdown("これは **重要** です"));
        Assert.Equal("*a* と *b*", Mrkdwn.FromMarkdown("__a__ と **b**"));
    }

    [Fact]
    public void 斜体はアンダースコアに()
    {
        Assert.Equal("_強調_ する", Mrkdwn.FromMarkdown("*強調* する"));
    }

    [Fact]
    public void 太字と斜体が混ざっても壊れない()
    {
        Assert.Equal("*太字* と _斜体_", Mrkdwn.FromMarkdown("**太字** と *斜体*"));
    }

    [Fact]
    public void 見出しは太字に()
    {
        Assert.Equal("*概要*\n本文", Mrkdwn.FromMarkdown("## 概要\n本文"));
    }

    [Fact]
    public void 箸条書きは中黒に_番号付きはそのまま()
    {
        Assert.Equal("• one\n• two\n  • nested\n1. three", Mrkdwn.FromMarkdown("- one\n* two\n  - nested\n1. three"));
    }

    [Fact]
    public void リンクはSlack形式に()
    {
        Assert.Equal("<https://example.com/a?b=1&amp;c=2|例>", Mrkdwn.FromMarkdown("[例](https://example.com/a?b=1&c=2)"));
    }

    [Fact]
    public void 本文のアンパサンドと角括弧はエスケープ()
    {
        Assert.Equal("a &lt; b &amp;&amp; c &gt; d", Mrkdwn.FromMarkdown("a < b && c > d"));
    }

    [Fact]
    public void コードブロックは言語名を落として中身をエスケープし装飾しない()
    {
        var md = "前\n```csharp\nvar x = a<b> && **c**;\n```\n後";
        Assert.Equal("前\n```\nvar x = a&lt;b&gt; &amp;&amp; **c**;\n```\n後", Mrkdwn.FromMarkdown(md));
    }

    [Fact]
    public void インラインコードは装飾しない()
    {
        Assert.Equal("`**raw**` と *b*", Mrkdwn.FromMarkdown("`**raw**` と **b**"));
    }

    [Fact]
    public void 表は等幅ブロックに変換され区切り行は消える()
    {
        var md = "| 名前 | 値 |\n|---|---|\n| a | 1 |\n| bb | 22 |";
        var expected = "```\n名前  値\na     1\nbb    22\n```";
        Assert.Equal(expected, Mrkdwn.FromMarkdown(md));
    }

    [Fact]
    public void 引用と取り消し線と水平線()
    {
        Assert.Equal("> 引用\n~消す~\n──────────", Mrkdwn.FromMarkdown("> 引用\n~~消す~~\n---"));
    }

    [Fact]
    public void 数式の乗算は斜体にならない()
    {
        Assert.Equal("2 * 3 * 4", Mrkdwn.FromMarkdown("2 * 3 * 4"));
    }
}
