using System.Text;
using Aizuchi.Anarlog;
using Aizuchi.Core;

public class AnarlogTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private const string Secret = "whsec_test";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static WebhookRequest Signed(string body, string? signature = null, long? timestamp = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-anarlog-signature"] = signature ?? Signature.Compute(Secret, bytes),
            ["x-anarlog-timestamp"] = (timestamp ?? Now.ToUnixTimeSeconds()).ToString(),
        };
        return new WebhookRequest(h => headers.GetValueOrDefault(h), bytes);
    }

    private static Task<WebhookOutcome> Run(WebhookRequest req, AnarlogWebhook? hook = null) =>
        (hook ?? new AnarlogWebhook(new AnarlogOptions(Secret, "C1"), new FixedClock(Now)))
            .HandleAsync(req, TestContext.Current!.Execution.CancellationToken);

    private const string Enhanced = """
        {"id":"evt_1","event":"note.enhanced","created_at":"2026-07-28T09:00:00.000Z","data":{
          "meeting":{"id":"m1","title":"週次定例","started_at":"2026-07-28T08:00:00Z",
            "note":{"title":"","markdown":"手書きメモ"},
            "summaries":[{"title":"Summary","markdown":"## 決まったこと\n- FAX を廃止"}],
            "action_items":[
              {"text":"移行手順を書く","status":"open","completed_at":null},
              {"text":"済んだやつ","status":"done","completed_at":"2026-07-28T09:00:00Z"}]},
          "transcript_text":"..."}}
        """;

    [Test]
    public async Task 署名はAnarlog自身のテストベクタと一致する()
    {
        // anarlog の plugins/local-api/src/dispatch.rs の signature_matches_reference_hmac と同じ値
        await Assert.That(Signature.Compute("whsec_test", Encoding.UTF8.GetBytes("""{"a":1}""")))
            .IsEqualTo("sha256=51426af50a41dd7ff2cd3f116594734766d4018d15d6fb07169aee5d2959adf5");
    }

    [Test]
    public async Task 署名が無い_違う_鍵が違うは全部401()
    {
        await Assert.That(await Run(Signed(Enhanced, signature: ""))).IsTypeOf<WebhookOutcome.Reject>();
        await Assert.That(await Run(Signed(Enhanced, signature: "sha256=00"))).IsTypeOf<WebhookOutcome.Reject>();
        var wrongKey = Signature.Compute("whsec_other", Encoding.UTF8.GetBytes(Enhanced));
        var r = (WebhookOutcome.Reject)await Run(Signed(Enhanced, signature: wrongKey));
        await Assert.That(r.Status).IsEqualTo(401);
        // 本文を 1 バイトでも変えれば通らない
        await Assert.That(await Run(new WebhookRequest(
            Signed(Enhanced).Header, Encoding.UTF8.GetBytes(Enhanced + " ")))).IsTypeOf<WebhookOutcome.Reject>();
    }

    [Test]
    public async Task 古いタイムスタンプは署名が正しくても捨てる()
    {
        var stale = Now.AddMinutes(-6).ToUnixTimeSeconds();
        var r = (WebhookOutcome.Reject)await Run(Signed(Enhanced, timestamp: stale));
        await Assert.That(r.Status).IsEqualTo(401);
        await Assert.That(r.Reason).Contains("タイムスタンプ");
        // 5 分以内なら通る。未来も同じ幅まで許す(時計のずれ)
        await Assert.That(await Run(Signed(Enhanced, timestamp: Now.AddMinutes(-4).ToUnixTimeSeconds()))).IsTypeOf<WebhookOutcome.Post>();
        await Assert.That(await Run(Signed(Enhanced, timestamp: Now.AddMinutes(4).ToUnixTimeSeconds()))).IsTypeOf<WebhookOutcome.Post>();
    }

    [Test]
    public async Task note_enhancedはタイトルと先頭の要約とアクションアイテムを投稿する()
    {
        var p = (WebhookOutcome.Post)await Run(Signed(Enhanced));
        await Assert.That(p.Channel).IsEqualTo("C1");
        await Assert.That(p.Markdown).StartsWith("**週次定例**\n\n## 決まったこと\n- FAX を廃止");
        await Assert.That(p.Markdown).Contains("**アクションアイテム**\n- 移行手順を書く");
        await Assert.That(p.Markdown).DoesNotContain("済んだやつ");        // 完了済みは出さない
        await Assert.That(p.Markdown).DoesNotContain("手書きメモ");        // 要約があればメモは使わない
        await Assert.That(p.Markdown).EndsWith("_Anarlog の会議メモ · 2026-07-28_");
    }

    [Test]
    public async Task 要約が無ければメモ_それも無ければ投稿しない()
    {
        var noteOnly = Enhanced.Replace("""[{"title":"Summary","markdown":"## 決まったこと\n- FAX を廃止"}]""", "[]");
        var p = (WebhookOutcome.Post)await Run(Signed(noteOnly));
        await Assert.That(p.Markdown).Contains("手書きメモ");

        var empty = noteOnly.Replace("""{"title":"","markdown":"手書きメモ"}""", "null");
        await Assert.That(await Run(Signed(empty))).IsTypeOf<WebhookOutcome.Ignore>();
    }

    [Test]
    public async Task 再送は一度しか投稿しない()
    {
        var hook = new AnarlogWebhook(new AnarlogOptions(Secret, "C1"), new FixedClock(Now));
        await Assert.That(await Run(Signed(Enhanced), hook)).IsTypeOf<WebhookOutcome.Post>();
        var again = await Run(Signed(Enhanced), hook);
        await Assert.That(again).IsTypeOf<WebhookOutcome.Ignore>();
        await Assert.That(((WebhookOutcome.Ignore)again).Reason).Contains("evt_1");
    }

    [Test]
    public async Task テスト配信は投稿し_録音終了と未知のイベントは黙って200()
    {
        var test = """{"id":"evt_t","event":"webhook.test","created_at":"x","data":{"message":"This is a test delivery from Anarlog."}}""";
        await Assert.That(((WebhookOutcome.Post)await Run(Signed(test))).Markdown).Contains("テスト配信");

        var completed = Enhanced.Replace("note.enhanced", "meeting.completed").Replace("evt_1", "evt_2");
        await Assert.That(await Run(Signed(completed))).IsTypeOf<WebhookOutcome.Ignore>();

        var unknown = Enhanced.Replace("note.enhanced", "something.new").Replace("evt_1", "evt_3");
        await Assert.That(await Run(Signed(unknown))).IsTypeOf<WebhookOutcome.Ignore>();
    }

    [Test]
    public async Task 壊れた本文は400_署名が合っていても()
    {
        var r = (WebhookOutcome.Reject)await Run(Signed("{not json"));
        await Assert.That(r.Status).IsEqualTo(400);
        var noId = (WebhookOutcome.Reject)await Run(Signed("""{"event":"note.enhanced"}"""));
        await Assert.That(noId.Status).IsEqualTo(400);
    }

    [Test]
    public async Task 設定は鍵とチャンネルの両方が要る()
    {
        static Func<string, string?> Env(params (string, string)[] pairs) => name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;
        await Assert.That(AnarlogOptions.FromEnvironment(Env())).IsNull();
        await Assert.That(() => AnarlogOptions.FromEnvironment(Env(("ANARLOG_WEBHOOK_SECRET", "whsec_x")))).Throws<ConfigException>();
        await Assert.That(() => AnarlogOptions.FromEnvironment(Env(("ANARLOG_SLACK_CHANNEL", "C1")))).Throws<ConfigException>();
        var o = AnarlogOptions.FromEnvironment(Env(("ANARLOG_WEBHOOK_SECRET", "whsec_x"), ("ANARLOG_SLACK_CHANNEL", "C1")))!;
        await Assert.That(o.Channel).IsEqualTo("C1");
    }
}
