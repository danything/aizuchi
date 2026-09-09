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
          {"id":42,"subject":"FAX の自動起票が失敗する","updatedAt":"2026-09-01T10:00:00Z",
           "_links":{"status":{"title":"New"},"type":{"title":"Bug"},"assignee":{"title":"澤田 澪"}}}]}}
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
        await Assert.That(pack.Tools.Count).IsEqualTo(4);
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
