using Aizuchi.Claude;
using Aizuchi.Core;
using Aizuchi.Slack;

// コネクタとプロバイダは環境変数で選ぶ。増えたらここに 1 行足す
var connectors = new Dictionary<string, Func<Func<string, string?>, ILogger, IChatConnector>>(StringComparer.OrdinalIgnoreCase)
{
    ["slack"] = (env, log) => new SlackConnector(
        new SlackApi(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, SlackOptions.FromEnvironment(env).BotToken),
        SlackOptions.FromEnvironment(env), log),
};
var providers = new Dictionary<string, Func<Func<string, string?>, ILlmProvider>>(StringComparer.OrdinalIgnoreCase)
{
    // 1 ターンが数分かかることもある。タイムアウトはストリーム全体に効くのでゆるめに
    ["claude"] = env => new ClaudeProvider(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }, ClaudeOptions.FromEnvironment(env)),
};

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var log = app.Logger;

IChatConnector connector;
ILlmProvider provider;
BotOptions options;
try
{
    Func<string, string?> env = Environment.GetEnvironmentVariable;
    var connectorName = Env.Or(env, "CHAT_CONNECTOR", "slack");
    var providerName = Env.Or(env, "LLM_PROVIDER", "claude");
    if (!connectors.TryGetValue(connectorName, out var makeConnector))
        throw new ConfigException($"CHAT_CONNECTOR={connectorName} は未対応です。使えるもの: {string.Join(", ", connectors.Keys)}");
    if (!providers.TryGetValue(providerName, out var makeProvider))
        throw new ConfigException($"LLM_PROVIDER={providerName} は未対応です。使えるもの: {string.Join(", ", providers.Keys)}");
    connector = makeConnector(env, log);
    provider = makeProvider(env);
    options = BotOptions.FromEnvironment(env);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

log.LogInformation("aizuchi 起動: connector={Connector} provider={Provider} max_history={MaxHistory}",
    connector.Name, provider.Name, options.MaxHistory);

var bot = new Bot(provider, options, log);
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => connector.Ready ? Results.Text("ok") : Results.StatusCode(503));

var stopping = app.Lifetime.ApplicationStopping;
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
