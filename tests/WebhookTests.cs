using System.Net;
using System.Text;
using Aizuchi.Core;
using Aizuchi.Slack;
using Microsoft.Extensions.Logging.Abstractions;

public class WebhookTests
{
    private sealed class FakeSource(string name, WebhookOutcome outcome) : IWebhookSource
    {
        public string Name => name;
        public WebhookRequest? Last;
        public Task<WebhookOutcome> HandleAsync(WebhookRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakePoster(Exception? fail = null) : IChannelPoster
    {
        public readonly List<(string Channel, string Markdown)> Posts = [];
        public Task PostAsync(string channel, string markdown, CancellationToken ct)
        {
            if (fail is not null) throw fail;
            Posts.Add((channel, markdown));
            return Task.CompletedTask;
        }
    }

    private static WebhookRequest Req(string body = "{}") =>
        new(_ => null, Encoding.UTF8.GetBytes(body));

    private static Task<WebhookEndpoint.Reply> Handle(WebhookEndpoint ep, string name, WebhookRequest? req = null) =>
        ep.HandleAsync(name, req ?? Req(), TestContext.Current!.Execution.CancellationToken);

    [Test]
    public async Task 知らない送り元は404_大きすぎる本文は413()
    {
        var ep = new WebhookEndpoint([new FakeSource("a", new WebhookOutcome.Ignore("x"))], new FakePoster(), NullLogger.Instance);
        await Assert.That((await Handle(ep, "b")).Status).IsEqualTo(404);
        await Assert.That((await Handle(ep, "A")).Status).IsEqualTo(200); // 名前の大小は見ない
        var huge = new WebhookRequest(_ => null, new byte[WebhookEndpoint.MaxBodyBytes + 1]);
        await Assert.That((await Handle(ep, "a", huge)).Status).IsEqualTo(413);
    }

    [Test]
    public async Task 投稿は200_拒否はその状態コード_投稿失敗は503で再送に任せる()
    {
        var poster = new FakePoster();
        var ok = new WebhookEndpoint([new FakeSource("a", new WebhookOutcome.Post("C1", "hi"))], poster, NullLogger.Instance);
        await Assert.That((await Handle(ok, "a")).Status).IsEqualTo(200);
        await Assert.That(poster.Posts).Count().IsEqualTo(1);
        await Assert.That(poster.Posts[0]).IsEqualTo(("C1", "hi"));

        var rejected = new WebhookEndpoint([new FakeSource("a", new WebhookOutcome.Reject(401, "bad"))], poster, NullLogger.Instance);
        var r = await Handle(rejected, "a");
        await Assert.That(r.Status).IsEqualTo(401);
        await Assert.That(r.Text).IsEqualTo("bad");
        await Assert.That(poster.Posts).Count().IsEqualTo(1); // 拒否では投稿しない

        var broken = new WebhookEndpoint([new FakeSource("a", new WebhookOutcome.Post("C1", "hi"))],
            new FakePoster(new SlackApiException("chat.postMessage", "channel_not_found")), NullLogger.Instance);
        await Assert.That((await Handle(broken, "a")).Status).IsEqualTo(503);
    }

    // ---- Slack 側の投稿口 ----

    private sealed class FakeSlack : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"ts":"1700000000.000100"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Test]
    public async Task Slackへの投稿は長ければ続きを最初の投稿のスレッドに入れる()
    {
        var fake = new FakeSlack();
        var connector = new SlackConnector(
            new SlackApi(new HttpClient(fake), "xoxb", NullLogger.Instance),
            new SlackOptions("xoxb", "xapp", true), 0, NullLogger.Instance);

        // 3,800 バイトを超えるので 2 通に分かれる
        var markdown = "**題名**\n\n" + new string('x', 3000) + "\n" + new string('y', 3000);
        await connector.PostAsync("C1", markdown, TestContext.Current!.Execution.CancellationToken);

        await Assert.That(fake.Bodies).Count().IsEqualTo(2);
        // 非 ASCII は \uXXXX でエスケープされて出るので、文字列比較ではなく JSON として読む
        var first = System.Text.Json.JsonDocument.Parse(fake.Bodies[0]).RootElement;
        var second = System.Text.Json.JsonDocument.Parse(fake.Bodies[1]).RootElement;
        await Assert.That(first.GetProperty("channel").GetString()).IsEqualTo("C1");
        await Assert.That(first.GetProperty("text").GetString()!).StartsWith("*題名*");       // Markdown → mrkdwn
        await Assert.That(first.TryGetProperty("thread_ts", out _)).IsFalse();                  // 最初はトップレベル
        await Assert.That(second.GetProperty("thread_ts").GetString()).IsEqualTo("1700000000.000100");
    }
}
