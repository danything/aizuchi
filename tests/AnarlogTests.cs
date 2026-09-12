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
    private static readonly AnarlogOptions Opt = new("C1", "unused", "https://azc.example.com/webhooks/anarlog");

    private static AnarlogStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "aizuchi-tests", Guid.NewGuid().ToString("N")));

    /// <summary>鍵を 1 本登録した状態の受け口</summary>
    private static AnarlogWebhook Hook(AnarlogStore? store = null)
    {
        store ??= TempStore();
        if (store.All().Count == 0) store.Add(Secret, "U1");
        return new AnarlogWebhook(store, Opt, new FixedClock(Now));
    }

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
        (hook ?? Hook()).HandleAsync(req, TestContext.Current!.Execution.CancellationToken);

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
        var hook = Hook();
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
    public async Task 設定はチャンネルだけで有効になり_鍵は環境変数に持たない()
    {
        static Func<string, string?> Env(params (string, string)[] pairs) => name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;
        await Assert.That(AnarlogOptions.FromEnvironment(Env())).IsNull();
        var o = AnarlogOptions.FromEnvironment(Env(("ANARLOG_SLACK_CHANNEL", "C1")))!;
        await Assert.That(o.Channel).IsEqualTo("C1");
        await Assert.That(o.StoreDir).IsEqualTo("data/anarlog");
        await Assert.That(o.PublicUrl).IsNull();
    }

    [Test]
    public async Task 登録が無ければ401_複数の鍵はどれかが合えば通る()
    {
        var empty = new AnarlogWebhook(TempStore(), Opt, new FixedClock(Now));
        var r = (WebhookOutcome.Reject)await Run(Signed(Enhanced), empty);
        await Assert.That(r.Status).IsEqualTo(401);
        await Assert.That(r.Reason).Contains("登録された署名鍵がありません");

        // 別デバイスの鍵で署名された配信も、その鍵が登録されていれば通る
        var store = TempStore();
        store.Add("whsec_deviceA", "U1");
        store.Add("whsec_deviceB", "U2");
        var hook = new AnarlogWebhook(store, Opt, new FixedClock(Now));
        var body = Encoding.UTF8.GetBytes(Enhanced);
        var byB = new WebhookRequest(Signed(Enhanced, signature: Signature.Compute("whsec_deviceB", body)).Header, body);
        await Assert.That(await Run(byB, hook)).IsTypeOf<WebhookOutcome.Post>();
    }

    [Test]
    public async Task 鍵の置き場は再起動しても残り_自分の登録しか消せない()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aizuchi-tests", Guid.NewGuid().ToString("N"));
        var a = new AnarlogStore(dir);
        var reg = a.Add("whsec_persist00", "U1");
        await Assert.That(reg.Id).HasLength().EqualTo(6);

        var b = new AnarlogStore(dir);                      // 別インスタンス = 再起動後
        await Assert.That(b.All()).Count().IsEqualTo(1);
        await Assert.That(b.All()[0].Secret).IsEqualTo("whsec_persist00");
        await Assert.That(b.Remove(reg.Id, "U2")).IsFalse();   // 他人は消せない
        await Assert.That(b.Remove(reg.Id, "U1")).IsTrue();
        await Assert.That(new AnarlogStore(dir).All()).Count().IsEqualTo(0);
    }

    // ---- Slack の手動コマンド ----

    private static Task<string?> Cmd(AnarlogCommand cmd, string text, string? user = "U1", bool dm = true) =>
        cmd.TryHandleAsync(new IncomingMessage("c", "C1", text, null!, user, dm), TestContext.Current!.Execution.CancellationToken);

    [Test]
    public async Task 鍵の登録はDMでしか受け付けず_登録すると受け口が通るようになる()
    {
        var store = TempStore();
        var cmd = new AnarlogCommand(store, Opt);

        await Assert.That(await Cmd(cmd, "こんにちは")).IsNull();                       // 関係ない発言は素通し
        var inChannel = await Cmd(cmd, "anarlog add whsec_0123456789abcdef", dm: false);
        await Assert.That(inChannel).Contains("DM で");
        await Assert.That(store.All()).Count().IsEqualTo(0);                          // チャンネルでは保存しない

        await Assert.That(await Cmd(cmd, "anarlog add whsec_")).Contains("形が違います");
        var ok = await Cmd(cmd, "anarlog add whsec_0123456789abcdef");
        await Assert.That(ok).Contains("登録しました");
        await Assert.That(ok).Contains("<#C1>");
        await Assert.That(ok).DoesNotContain("0123456789abcdef");                    // 鍵は返信に出さない
        await Assert.That(store.All()[0].SlackUser).IsEqualTo("U1");

        // その鍵で署名された配信が通る
        var body = Encoding.UTF8.GetBytes(Enhanced);
        var req = new WebhookRequest(Signed(Enhanced, signature: Signature.Compute("whsec_0123456789abcdef", body)).Header, body);
        await Assert.That(await Run(req, new AnarlogWebhook(store, Opt, new FixedClock(Now)))).IsTypeOf<WebhookOutcome.Post>();
    }

    [Test]
    public async Task 一覧と削除は自分の分だけ_案内にはURLが入る()
    {
        var store = TempStore();
        var mine = store.Add("whsec_aaaaaaaaaa", "U1");
        store.Add("whsec_bbbbbbbbbb", "U2");
        var cmd = new AnarlogCommand(store, Opt);

        var list = (await Cmd(cmd, "anarlog"))!;
        await Assert.That(list).Contains("1 件");
        await Assert.That(list).Contains(mine.Id);
        await Assert.That(list).Contains("https://azc.example.com/webhooks/anarlog");
        await Assert.That(list).DoesNotContain("whsec_aaaaaaaaaa");

        await Assert.That(await Cmd(cmd, $"anarlog remove {mine.Id}", user: "U2")).Contains("ありません");
        await Assert.That(await Cmd(cmd, $"anarlog remove {mine.Id}")).Contains("消しました");
        await Assert.That(store.All()).Count().IsEqualTo(1);
    }
}
