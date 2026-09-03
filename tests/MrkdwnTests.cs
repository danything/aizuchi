using Aizuchi.Slack;
using System.Threading.Tasks;

public class MrkdwnTests
{
    [Test]
    public async Task 太字は二重アスタリスクから一重に()
    {
        await Assert.That(Mrkdwn.FromMarkdown("これは **重要** です")).IsEqualTo("これは *重要* です");
        await Assert.That(Mrkdwn.FromMarkdown("__a__ と **b**")).IsEqualTo("*a* と *b*");
    }

    [Test]
    public async Task 斜体はアンダースコアに()
    {
        await Assert.That(Mrkdwn.FromMarkdown("*強調* する")).IsEqualTo("_強調_ する");
    }

    [Test]
    public async Task 太字と斜体が混ざっても壊れない()
    {
        await Assert.That(Mrkdwn.FromMarkdown("**太字** と *斜体*")).IsEqualTo("*太字* と _斜体_");
    }

    [Test]
    public async Task 見出しは太字に()
    {
        await Assert.That(Mrkdwn.FromMarkdown("## 概要\n本文")).IsEqualTo("*概要*\n本文");
    }

    [Test]
    public async Task 箸条書きは中黒に_番号付きはそのまま()
    {
        await Assert.That(Mrkdwn.FromMarkdown("- one\n* two\n  - nested\n1. three")).IsEqualTo("• one\n• two\n  • nested\n1. three");
    }

    [Test]
    public async Task リンクはSlack形式に()
    {
        await Assert.That(Mrkdwn.FromMarkdown("[例](https://example.com/a?b=1&c=2)")).IsEqualTo("<https://example.com/a?b=1&amp;c=2|例>");
    }

    [Test]
    public async Task 本文のアンパサンドと角括弧はエスケープ()
    {
        await Assert.That(Mrkdwn.FromMarkdown("a < b && c > d")).IsEqualTo("a &lt; b &amp;&amp; c &gt; d");
    }

    [Test]
    public async Task コードブロックは言語名を落として中身をエスケープし装飾しない()
    {
        var md = "前\n```csharp\nvar x = a<b> && **c**;\n```\n後";
        await Assert.That(Mrkdwn.FromMarkdown(md)).IsEqualTo("前\n```\nvar x = a&lt;b&gt; &amp;&amp; **c**;\n```\n後");
    }

    [Test]
    public async Task インラインコードは装飾しない()
    {
        await Assert.That(Mrkdwn.FromMarkdown("`**raw**` と **b**")).IsEqualTo("`**raw**` と *b*");
    }

    [Test]
    public async Task 表は等幅ブロックに変換され区切り行は消える()
    {
        var md = "| 名前 | 値 |\n|---|---|\n| a | 1 |\n| bb | 22 |";
        var expected = "```\n名前  値\na     1\nbb    22\n```";
        await Assert.That(Mrkdwn.FromMarkdown(md)).IsEqualTo(expected);
    }

    [Test]
    public async Task 引用と取り消し線と水平線()
    {
        await Assert.That(Mrkdwn.FromMarkdown("> 引用\n~~消す~~\n---")).IsEqualTo("> 引用\n~消す~\n──────────");
    }

    [Test]
    public async Task 数式の乗算は斜体にならない()
    {
        await Assert.That(Mrkdwn.FromMarkdown("2 * 3 * 4")).IsEqualTo("2 * 3 * 4");
    }
}