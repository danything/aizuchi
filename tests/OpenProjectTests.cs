using System.Net;
using System.Text;
using System.Text.Json;
using Aizuchi.Core;
using Aizuchi.OpenProject;

public class OpenProjectTests
{
    /// <summary>叩かれたパスに応じて決めた JSON を返す偽 OpenProject</summary>
    private sealed class FakeOp : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Status, string Body)> Routes = new();
        public readonly List<string> Calls = [];
        public readonly List<string?> Auths = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add(path);
            Auths.Add(request.Headers.Authorization?.Parameter);
            var key = Routes.Keys.Where(k => path.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            var (status, body) = key is null ? (HttpStatusCode.NotFound, "{}") : Routes[key];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Wp = """
        {"total":2,"count":1,"_embedded":{"elements":[
          {"id":42,"subject":"FAX の自動起票が失敗する","updatedAt":"2026-09-01T10:00:00Z","storyPoints":3,"percentageDone":40,
           "_links":{"status":{"title":"New"},"type":{"title":"Bug"},"assignee":{"title":"澤田 澪"},"sprint":{"title":"Sprint 12"}}}]}}
        """;

    // 実物と同じ形。スプリント API は他プロジェクト(#4)定義のものだけを返す
    private const string Sprints = """
        {"total":2,"_embedded":{"elements":[
          {"id":2,"name":"Rebuilt 1","startDate":"2026-07-18",
           "_links":{"status":{"href":"urn:openproject-org:api:v3:sprints:status:completed"},
                     "definingWorkspace":{"href":"/api/v3/projects/4"}}},
          {"id":1,"name":"Nissan 1","startDate":"2026-07-18",
           "_links":{"status":{"href":"urn:openproject-org:api:v3:sprints:status:active"},
                     "definingWorkspace":{"href":"/api/v3/projects/4"}}}]}}
        """;

    // 割当は一覧 API に出てこないスプリント。これが実態
    private const string Closed = """
        {"total":4,"_embedded":{"elements":[
          {"id":1,"subject":"a","storyPoints":3,"_links":{"status":{"title":"終了"},"sprint":{"href":"/api/v3/sprints/7","title":"Sprints 7"}}},
          {"id":2,"subject":"b","storyPoints":5,"_links":{"status":{"title":"終了"},"sprint":{"href":"/api/v3/sprints/7","title":"Sprints 7"}}},
          {"id":3,"subject":"c","_links":{"status":{"title":"終了"},"sprint":{"href":"/api/v3/sprints/6","title":"Sprints 6"}}},
          {"id":4,"subject":"d","storyPoints":2,"_links":{"status":{"title":"終了"}}}]}}
        """;

    private static async Task<(OpenProjectToolPack Pack, FakeOp Fake)> Pack()
    {
        var fake = new FakeOp();
        fake.Routes["/api/v3/users/me"] = (HttpStatusCode.OK, """{"id":1,"name":"aizuchi bot"}""");
        var client = new OpenProjectClient(new HttpClient(fake), new OpenProjectOptions("https://op.example.com", "key123"));
        return (await OpenProjectToolPack.CreateAsync(client, TestContext.Current!.Execution.CancellationToken), fake);
    }

    private static Task<ToolResult> Run(OpenProjectToolPack pack, string name, string args) =>
        pack.Tools.First(t => t.Name == name).InvokeAsync(
            JsonDocument.Parse(args).RootElement, TestContext.Current!.Execution.CancellationToken);

    [Test]
    public async Task URLとAPIキーは両方要る()
    {
        static Func<string, string?> Env(params (string, string)[] pairs) => name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;
        await Assert.That(OpenProjectOptions.FromEnvironment(Env())).IsNull();
        await Assert.That(() => OpenProjectOptions.FromEnvironment(Env(("OPENPROJECT_URL", "https://op.example.com")))).Throws<ConfigException>();
        await Assert.That(() => OpenProjectOptions.FromEnvironment(Env(("OPENPROJECT_API_KEY", "k")))).Throws<ConfigException>();
        // セルフホスト前提。絶対 URL でなければ弾く
        await Assert.That(() => OpenProjectOptions.FromEnvironment(Env(("OPENPROJECT_URL", "op.example.com"), ("OPENPROJECT_API_KEY", "k")))).Throws<ConfigException>();
        var o = OpenProjectOptions.FromEnvironment(Env(("OPENPROJECT_URL", "https://op.example.com/"), ("OPENPROJECT_API_KEY", "k")))!;
        await Assert.That(o.Url).IsEqualTo("https://op.example.com"); // 末尾のスラッシュは落とす
    }

    [Test]
    public async Task APIキーはapikeyのパスワードとしてBasicで送る()
    {
        var (_, fake) = await Pack();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(fake.Auths[0]!));
        await Assert.That(decoded).IsEqualTo("apikey:key123");
        await Assert.That(fake.Calls[0]).IsEqualTo("/api/v3/users/me");
    }

    [Test]
    public async Task 道具のスキーマは正しいJSONで_プロンプトにURLが出る()
    {
        var (pack, _) = await Pack();
        await Assert.That(pack.Tools.Count).IsEqualTo(6);
        foreach (var t in pack.Tools)
            await Assert.That(JsonDocument.Parse(t.InputSchemaJson).RootElement.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(pack.PromptSection).Contains("https://op.example.com");
        await Assert.That(pack.PromptSection).Contains("aizuchi bot");
    }

    [Test]
    public async Task 一覧はリンク付きの1行にまとめる()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, Wp);
        var r = await Run(pack, "openproject_list", """{"project":"shop"}""");
        await Assert.That(r.IsError).IsFalse();
        await Assert.That(r.Content).Contains("#42 (New) FAX の自動起票が失敗する [Bug]");
        await Assert.That(r.Content).Contains("担当: 澤田 澪");
        await Assert.That(r.Content).Contains("<https://op.example.com/work_packages/42>");
        await Assert.That(r.Content).StartsWith("2 件中");
        // 既定は未完了だけ。status は値ではなく演算子で表す
        await Assert.That(Uri.UnescapeDataString(fake.Calls[^1])).Contains("""{"status":{"operator":"o","values":[]}}""");
    }

    [Test]
    public async Task 検索はsearchで試してから件名に落とす()
    {
        var (pack, fake) = await Pack();
        // search フィルタを知らないバージョンを模す(400 が返る)
        fake.Routes["/api/v3/work_packages"] = (HttpStatusCode.BadRequest, """{"message":"unknown filter"}""");
        var r = await Run(pack, "openproject_search", """{"query":"FAX"}""");
        await Assert.That(r.IsError).IsTrue(); // 2 回目も 400 なら素直に失敗する

        var tried = fake.Calls.TakeLast(2).Select(Uri.UnescapeDataString).ToList();
        await Assert.That(tried[0]).Contains("""{"search":{"operator":"**","values":["FAX"]}}""");
        await Assert.That(tried[1]).Contains("""{"subjectOrId":{"operator":"**","values":["FAX"]}}""");
    }

    [Test]
    public async Task 検索がsearchで通れば落とさない()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/work_packages"] = (HttpStatusCode.OK, Wp);
        var r = await Run(pack, "openproject_search", """{"query":"FAX","status":"all"}""");
        await Assert.That(r.IsError).IsFalse();
        await Assert.That(fake.Calls.Count(c => c.StartsWith("/api/v3/work_packages"))).IsEqualTo(1);
        await Assert.That(Uri.UnescapeDataString(fake.Calls[^1])).Contains("""{"status":{"operator":"*","values":[]}}""");
    }

    [Test]
    public async Task 詳細は本文と人が書いたコメントだけを返す()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/work_packages/42/activities"] = (HttpStatusCode.OK, """
            {"total":2,"_embedded":{"elements":[
              {"id":1,"comment":{"raw":"再現しました"},"createdAt":"2026-09-02T00:00:00Z","_links":{"user":{"title":"田中"}}},
              {"id":2,"comment":{"raw":""},"createdAt":"2026-09-03T00:00:00Z","_links":{"user":{"title":"田中"}}}]}}
            """);
        fake.Routes["/api/v3/work_packages/42"] = (HttpStatusCode.OK, """
            {"id":42,"subject":"FAX の自動起票が失敗する","description":{"raw":"月末だけ落ちる"},
             "createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-05T00:00:00Z",
             "_links":{"status":{"title":"New"},"type":{"title":"Bug"},"priority":{"title":"High"},"project":{"title":"店舗"}}}
            """);
        var r = await Run(pack, "openproject_get", """{"id":42}""");
        await Assert.That(r.Content).Contains("#42 (New) FAX の自動起票が失敗する");
        await Assert.That(r.Content).Contains("Bug / High, プロジェクト: 店舗");
        await Assert.That(r.Content).Contains("月末だけ落ちる");
        await Assert.That(r.Content).Contains("コメント (1 件):"); // 属性変更だけの履歴は数えない
        await Assert.That(r.Content).Contains("[田中] (2026-09-02) 再現しました");
    }

    [Test]
    public async Task 一覧にストーリーポイントとスプリントが出る()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, Wp);
        var r = await Run(pack, "openproject_list", """{"project":"shop"}""");
        await Assert.That(r.Content).Contains("SP3");
        await Assert.That(r.Content).Contains("Sprint 12");
        await Assert.That(r.Content).Contains("進捗 40%");
    }

    [Test]
    public async Task スプリントで絞れてページ番号で送る()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, Wp);
        await Run(pack, "openproject_list", """{"project":"shop","sprint":8,"page":2}""");
        var call = Uri.UnescapeDataString(fake.Calls[^1]);
        await Assert.That(call).Contains("""{"sprint":{"operator":"=","values":["8"]}}""");
        // offset はページ番号(1 始まり)。件数のオフセットではない
        await Assert.That(call).Contains("offset=2");
    }

    [Test]
    public async Task スプリント一覧はAPIの返しと実際の割当を並べる()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/sprints"] = (HttpStatusCode.OK, Sprints);
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, Closed);
        var r = await Run(pack, "openproject_sprints", """{"project":"shop"}""");

        await Assert.That(r.Content).Contains("- #1 Nissan 1 (2026-07-18 〜 -) active (定義元プロジェクト #4) ← 進行中");
        await Assert.That(r.Content).Contains("- #2 Rebuilt 1");
        // 一覧 API に出てこないスプリントが実際には使われている、が見えること
        await Assert.That(r.Content).Contains("Sprints 7: 2 件");
        await Assert.That(r.Content).Contains("Sprints 6: 1 件");
        await Assert.That(r.Content).Contains("スプリント未割当: 1 件");
    }

    [Test]
    public async Task ベロシティは一覧APIではなく実際の割当で束ねる()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/sprints"] = (HttpStatusCode.OK, Sprints);
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, Closed);
        var r = await Run(pack, "openproject_velocity", """{"project":"shop"}""");
        await Assert.That(r.IsError).IsFalse();

        // 一覧 API が返す Nissan 1 / Rebuilt 1 ではなく、割当先で束ねる
        await Assert.That(r.Content).Contains("- Sprints 7: 8 SP / 完了 2 件");
        await Assert.That(r.Content).Contains("- Sprints 6: 0 SP / 完了 1 件(storyPoints 設定済み 0 件)");
        await Assert.That(r.Content).Contains("- スプリント未割当: 2 SP / 完了 1 件");
        await Assert.That(r.Content).DoesNotContain("Nissan 1");
        // 未割当は最後
        await Assert.That(r.Content.IndexOf("Sprints 6")).IsLessThan(r.Content.IndexOf("スプリント未割当"));
        // クローズ扱いだけを数える
        await Assert.That(Uri.UnescapeDataString(fake.Calls[^1])).Contains("""{"status":{"operator":"c","values":[]}}""");
    }

    [Test]
    public async Task storyPointsが全部未設定なら算出できないと言う()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/projects/shop/work_packages"] = (HttpStatusCode.OK, """
            {"total":2,"_embedded":{"elements":[
              {"id":1,"subject":"a","_links":{"status":{"title":"終了"},"sprint":{"href":"/api/v3/sprints/7","title":"Sprints 7"}}},
              {"id":2,"subject":"b","_links":{"status":{"title":"終了"},"sprint":{"href":"/api/v3/sprints/7","title":"Sprints 7"}}}]}}
            """);
        var r = await Run(pack, "openproject_velocity", """{"project":"shop"}""");
        await Assert.That(r.Content).Contains("storyPoints が 1 件も設定されていません");
        await Assert.That(r.Content).Contains("Story types");
    }

    [Test]
    public async Task 詳細にストーリーポイントが出る()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/work_packages/42/activities"] = (HttpStatusCode.OK, """{"total":0,"_embedded":{"elements":[]}}""");
        fake.Routes["/api/v3/work_packages/42"] = (HttpStatusCode.OK, """
            {"id":42,"subject":"件名","storyPoints":8,
             "_links":{"status":{"title":"New"},"version":{"title":"Sprint 12"}}}
            """);
        var r = await Run(pack, "openproject_get", """{"id":42}""");
        // sprint リンクが無ければ version で代用する
        await Assert.That(r.Content).Contains("ストーリーポイント: 8, スプリント: Sprint 12");
    }

    [Test]
    public async Task 認証と権限の失敗は理由が分かる文言で返す()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/api/v3/work_packages/9"] = (HttpStatusCode.Unauthorized, "{}");
        var r = await Run(pack, "openproject_get", """{"id":9}""");
        await Assert.That(r.IsError).IsTrue();
        await Assert.That(r.Content).Contains("API キーが通りません");

        fake.Routes["/api/v3/work_packages/8"] = (HttpStatusCode.Forbidden, "{}");
        await Assert.That((await Run(pack, "openproject_get", """{"id":8}""")).Content).Contains("権限");
    }
}
