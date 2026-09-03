using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aizuchi.Core;
using Aizuchi.GitHub;

public class GitHubTests
{
    /// <summary>叩かれたパスに応じて決めた JSON を返す偽 GitHub</summary>
    private sealed class FakeGitHub : HttpMessageHandler
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
            var (status, body) = key is null ? (HttpStatusCode.NotFound, """{"message":"Not Found"}""") : Routes[key];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedAuth(Dictionary<string, string> owners) : IGitHubAuth
    {
        public Task<IReadOnlyDictionary<string, string>> OwnersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(owners);
        public Task<string> TokenForAsync(string owner, CancellationToken ct) => Task.FromResult("tok-" + owner);
    }

    private static async Task<(GitHubToolPack Pack, FakeGitHub Fake)> Pack()
    {
        var fake = new FakeGitHub();
        var client = new GitHubClient(new HttpClient(fake), new FixedAuth(new() { ["danything"] = "Organization", ["5ym"] = "User" }));
        return (await GitHubToolPack.CreateAsync(client, CancellationToken.None), fake);
    }

    private static ITool Tool(GitHubToolPack pack, string name) => pack.Tools.Single(t => t.Name == name);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task JWTはRS256で署名され_iatが60秒戻っている()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var jwt = GitHubJwt.Create("12345", pem, now);
        var parts = jwt.Split('.');
        await Assert.That(parts.Length).IsEqualTo(3);

        static byte[] FromB64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        var header = JsonDocument.Parse(FromB64Url(parts[0])).RootElement;
        var payload = JsonDocument.Parse(FromB64Url(parts[1])).RootElement;
        await Assert.That(header.GetProperty("alg").GetString()).IsEqualTo("RS256");
        await Assert.That(payload.GetProperty("iss").GetString()).IsEqualTo("12345");
        await Assert.That(payload.GetProperty("iat").GetInt64()).IsEqualTo(1_700_000_000 - 60);
        await Assert.That(payload.GetProperty("exp").GetInt64()).IsEqualTo(1_700_000_000 + 540);
        var ok = rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task Appはinstallationを拾いトークンを使い回す()
    {
        using var rsa = RSA.Create(2048);
        var fake = new FakeGitHub();
        fake.Routes["/app/installations"] = (HttpStatusCode.OK, """[{"id":11,"account":{"login":"danything","type":"Organization"}},{"id":22,"account":{"login":"5ym","type":"User"}},{"id":33,"account":{"login":"other","type":"Organization"}}]""");
        fake.Routes["/app/installations/11/access_tokens"] = (HttpStatusCode.OK, """{"token":"ghs_dany","expires_at":"2099-01-01T00:00:00Z"}""");
        var auth = new AppAuth(new HttpClient(fake), "1", rsa.ExportRSAPrivateKeyPem(), ["danything", "5ym"]);

        var owners = await auth.OwnersAsync(CancellationToken.None);
        await Assert.That(owners.Keys).IsEquivalentTo(["danything", "5ym"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(owners["danything"]).IsEqualTo("Organization");

        await Assert.That(await auth.TokenForAsync("DanyThing", CancellationToken.None)).IsEqualTo("ghs_dany");
        await Assert.That(await auth.TokenForAsync("danything", CancellationToken.None)).IsEqualTo("ghs_dany");
        await Assert.That(fake.Calls.Count(c => c.Contains("access_tokens"))).IsEqualTo(1);
        await Assert.That(fake.Auths[0]!.Split('.').Length).IsEqualTo(3); // installations は JWT で
    }

    [Test]
    public async Task 読めないownerは断る()
    {
        var (pack, _) = await Pack();
        var r = await Tool(pack, "github_list").InvokeAsync(Args("""{"repo":"someone/else","kind":"issues"}"""), CancellationToken.None);
        await Assert.That(r.IsError).IsTrue();
        await Assert.That(r.Content).Contains("danything, 5ym");
        var bad = await Tool(pack, "github_get").InvokeAsync(Args("""{"repo":"noslash","number":1}"""), CancellationToken.None);
        await Assert.That(bad.IsError).IsTrue();
    }

    [Test]
    public async Task 一覧はPRを落として整形する()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/repos/danything/denpa/issues?state=open"] = (HttpStatusCode.OK, """
            [{"number":5,"title":"録画が落ちる","state":"open","user":{"login":"5ym"},"labels":[{"name":"bug"}],"updated_at":"2026-09-01T00:00:00Z","html_url":"https://github.com/danything/denpa/issues/5"},
             {"number":6,"title":"PR です","state":"open","user":{"login":"5ym"},"labels":[],"updated_at":"2026-09-02T00:00:00Z","html_url":"https://github.com/danything/denpa/pull/6","pull_request":{"url":"x"}}]
            """);
        var r = await Tool(pack, "github_list").InvokeAsync(Args("""{"repo":"danything/denpa","kind":"issues"}"""), CancellationToken.None);
        await Assert.That(r.IsError).IsFalse();
        await Assert.That(r.Content).IsEqualTo("- Issue #5 (open) 録画が落ちる [bug] by 5ym, updated 2026-09-01 <https://github.com/danything/denpa/issues/5>\n");
        await Assert.That(fake.Auths[0]).IsEqualTo("tok-danything");
    }

    [Test]
    public async Task ファイルは行番号付きで範囲を切り出す()
    {
        var (pack, fake) = await Pack();
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("line1\nline2\nline3\nline4\n"));
        fake.Routes["/repos/danything/aizuchi/contents/src/Bot.cs"] = (HttpStatusCode.OK, $$"""{"type":"file","size":24,"encoding":"base64","content":"{{content}}","path":"src/Bot.cs","html_url":"https://github.com/danything/aizuchi/blob/main/src/Bot.cs"}""");
        var r = await Tool(pack, "github_read_file").InvokeAsync(Args("""{"repo":"danything/aizuchi","path":"src/Bot.cs","start_line":2,"end_line":3}"""), CancellationToken.None);
        await Assert.That(r.Content).IsEqualTo("<https://github.com/danything/aizuchi/blob/main/src/Bot.cs> 行 2-3 / 全 5 行\n```\n2: line2\n3: line3\n```\n…続きは start_line=4 で\n");
    }

    [Test]
    public async Task 検索はownerの種別で修飾子を変える()
    {
        var (pack, fake) = await Pack();
        fake.Routes["/search/code"] = (HttpStatusCode.OK, """{"total_count":1,"items":[{"path":"src/a.cs","repository":{"full_name":"danything/aizuchi"},"html_url":"u"}]}""");
        var r = await Tool(pack, "github_search").InvokeAsync(Args("""{"kind":"code","query":"WebSocket","limit":5}"""), CancellationToken.None);
        await Assert.That(r.Content).StartsWith("2 件中");
        await Assert.That(fake.Calls[0]).Contains("q=WebSocket%20org%3Adanything");
        await Assert.That(fake.Calls[1]).Contains("q=WebSocket%20user%3A5ym");

        fake.Calls.Clear();
        fake.Routes["/search/issues"] = (HttpStatusCode.OK, """{"total_count":0,"items":[]}""");
        await Tool(pack, "github_search").InvokeAsync(Args("""{"kind":"prs","query":"fix","repo":"danything/denpa"}"""), CancellationToken.None);
        await Assert.That(fake.Calls.Single()).Contains("q=fix%20repo%3Adanything%2Fdenpa%20is%3Apr");
    }

    [Test]
    public async Task 見つからないときはツールの失敗として返し例外にしない()
    {
        var (pack, _) = await Pack();
        var r = await Tool(pack, "github_commits").InvokeAsync(Args("""{"repo":"danything/nothing"}"""), CancellationToken.None);
        await Assert.That(r.IsError).IsTrue();
        await Assert.That(r.Content).Contains("見つかりません");
    }

    [Test]
    public async Task 設定の解釈()
    {
        static Func<string, string?> Env(params (string, string)[] pairs) => name => pairs.FirstOrDefault(p => p.Item1 == name).Item2;
        await Assert.That(GitHubOptions.FromEnvironment(Env())).IsNull();
        await Assert.That(() => GitHubOptions.FromEnvironment(Env(("GITHUB_APP_ID", "1")))).Throws<ConfigException>();
        await Assert.That(() => GitHubOptions.FromEnvironment(Env(("GITHUB_TOKEN", "t")))).Throws<ConfigException>();
        var app = GitHubOptions.FromEnvironment(Env(("GITHUB_APP_ID", "1"), ("GITHUB_APP_PRIVATE_KEY", "pem"), ("GITHUB_OWNERS", "DanyThing, 5ym")))!;
        await Assert.That(app.Owners).IsEquivalentTo(["danything", "5ym"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var pat = GitHubOptions.FromEnvironment(Env(("GITHUB_TOKEN", "t"), ("GITHUB_OWNERS", "danything")))!;
        await Assert.That(pat.Token).IsEqualTo("t");
    }

    [Test]
    public async Task 道具のスキーマは正しいJSONで_プロンプトにownerが出る()
    {
        var (pack, _) = await Pack();
        await Assert.That(pack.Tools.Count).IsEqualTo(6);
        foreach (var t in pack.Tools)
            await Assert.That(JsonDocument.Parse(t.InputSchemaJson).RootElement.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(pack.PromptSection).Contains("danything, 5ym");
    }
}
