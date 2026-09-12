using Aizuchi.Anarlog;
using Aizuchi.Claude;
using Aizuchi.Core;
using Aizuchi.GitHub;
using Aizuchi.OpenProject;
using Aizuchi.Slack;
using System.Security.Cryptography;

// コネクタとプロバイダは環境変数で選ぶ。増えたらここに 1 行足す
var connectors = new Dictionary<string, Func<Func<string, string?>, BotOptions, ILogger, IChatConnector>>(StringComparer.OrdinalIgnoreCase)
{
    ["slack"] = (env, bot, log) => new SlackConnector(
        new SlackApi(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, SlackOptions.FromEnvironment(env).BotToken, log),
        SlackOptions.FromEnvironment(env), bot.ChannelContext, log),
};
var providers = new Dictionary<string, Func<Func<string, string?>, ILlmProvider>>(StringComparer.OrdinalIgnoreCase)
{
    // 1 ターンが数分かかることもある。タイムアウトはストリーム全体に効くのでゆるめに
    ["claude"] = env => new ClaudeProvider(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }, ClaudeOptions.FromEnvironment(env)),
};

var builder = WebApplication.CreateSlimBuilder(args);
// /healthz /readyz のプローブが info で毎回出て本体のログが埋もれるので、ASP.NET 側は警告以上だけ
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();
var log = app.Logger;
var stopping = app.Lifetime.ApplicationStopping;

IChatConnector connector;
ILlmProvider provider;
BotOptions options;
GitHubOptions? github;
OpenProjectOptions? openProject;
AnarlogOptions? anarlog;
try
{
    Func<string, string?> env = Environment.GetEnvironmentVariable;
    var connectorName = Env.Or(env, "CHAT_CONNECTOR", "slack");
    var providerName = Env.Or(env, "LLM_PROVIDER", "claude");
    if (!connectors.TryGetValue(connectorName, out var makeConnector))
        throw new ConfigException($"CHAT_CONNECTOR={connectorName} は未対応です。使えるもの: {string.Join(", ", connectors.Keys)}");
    if (!providers.TryGetValue(providerName, out var makeProvider))
        throw new ConfigException($"LLM_PROVIDER={providerName} は未対応です。使えるもの: {string.Join(", ", providers.Keys)}");
    options = BotOptions.FromEnvironment(env);
    connector = makeConnector(env, options, log);
    provider = makeProvider(env);
    github = GitHubOptions.FromEnvironment(env);
    openProject = OpenProjectOptions.FromEnvironment(env);
    anarlog = AnarlogOptions.FromEnvironment(env);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

IMemoryStore? memory = null;
if (options.MemoryDir is { } memoryDir)
{
    try
    {
        Directory.CreateDirectory(memoryDir);
        memory = new FileMemoryStore(memoryDir);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"記憶の置き場 {memoryDir} を作れません({ex.Message})。BOT_MEMORY_DIR=off で無効にできます。");
        return 1;
    }
}

log.LogInformation("aizuchi 起動: connector={Connector} provider={Provider} max_history={MaxHistory} memory={Memory} channel_context={ChannelContext}",
    connector.Name, provider.Name, options.MaxHistory, options.MemoryDir ?? "off", options.ChannelContext);

// 道具パック。GitHub は設定があるときだけ(起動時に installation を引いて読める owner を確定する)
var packs = new List<IToolPack>();
if (github is not null)
{
    var ghHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    IGitHubAuth auth = github.Token is { } pat
        ? new TokenAuth(ghHttp, pat, github.Owners)
        : new AppAuth(ghHttp, github.AppId!, github.PrivateKeyPem!, github.Owners);
    try
    {
        var pack = await GitHubToolPack.CreateAsync(new GitHubClient(ghHttp, auth), stopping);
        packs.Add(pack);
        log.LogInformation("GitHub: {Auth} で {Owners} を読める", github.Token is null ? "App" : "PAT", string.Join(", ", (await auth.OwnersAsync(stopping)).Keys));
    }
    catch (Exception ex) when (ex is GitHubException or HttpRequestException or CryptographicException)
    {
        Console.Error.WriteLine($"GitHub の認証に失敗しました: {ex.Message}");
        return 1;
    }
}

if (openProject is not null)
{
    var opHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    try
    {
        packs.Add(await OpenProjectToolPack.CreateAsync(new OpenProjectClient(opHttp, openProject), stopping));
        log.LogInformation("OpenProject: {Url} を読める", openProject.Url);
    }
    catch (Exception ex) when (ex is OpenProjectException or HttpRequestException)
    {
        Console.Error.WriteLine($"OpenProject に接続できません: {ex.Message}");
        return 1;
    }
}

var bot = new Bot(provider, options, memory, packs, log);
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => connector.Ready ? Results.Text("ok") : Results.StatusCode(503));

// webhook の受け口。設定のある送り元だけ /webhooks/{name} を出す(無ければ経路自体を作らない)
var sources = new List<IWebhookSource>();
if (anarlog is not null) sources.Add(new AnarlogWebhook(anarlog));
if (sources.Count > 0)
{
    if (connector is not IChannelPoster poster)
    {
        Console.Error.WriteLine($"コネクタ {connector.Name} はチャンネル投稿に対応していないので webhook を受けられません");
        return 1;
    }
    var endpoint = new WebhookEndpoint(sources, poster, log);
    app.MapPost("/webhooks/{name}", async (string name, HttpRequest request, CancellationToken ct) =>
    {
        var body = await ReadBounded(request, WebhookEndpoint.MaxBodyBytes, ct);
        if (body is null) return Results.StatusCode(413);
        var reply = await endpoint.HandleAsync(name,
            new WebhookRequest(h => request.Headers[h].FirstOrDefault(), body.Value), ct);
        return Results.Text(reply.Text, statusCode: reply.Status);
    });
    log.LogInformation("webhook 受信: {Paths}", string.Join(", ", sources.Select(s => "/webhooks/" + s.Name)));
}

var run = Task.Run(async () =>
{
    try
    {
        await connector.RunAsync(bot, stopping);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // ログイン失敗など、再接続では直らないもの。落として CrashLoop で気付けるようにする
        log.LogCritical(ex, "コネクタが停止しました");
        app.Lifetime.StopApplication();
    }
}, stopping);

app.Run();
await run;
return connector.Ready ? 0 : 1;

/// <summary>本文を上限まで読む。超えたら null(413 にする)。署名検証のため生のまま持つ</summary>
static async Task<ReadOnlyMemory<byte>?> ReadBounded(HttpRequest request, int max, CancellationToken ct)
{
    if (request.ContentLength > max) return null;
    var ms = new MemoryStream();
    var buf = new byte[16 * 1024];
    int n;
    while ((n = await request.Body.ReadAsync(buf, ct)) > 0)
    {
        ms.Write(buf, 0, n);
        if (ms.Length > max) return null;
    }
    return ms.ToArray();
}
