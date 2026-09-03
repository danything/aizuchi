using SlackClaudeBot.Bot;
using SlackClaudeBot.Claude;
using SlackClaudeBot.Slack;

BotConfig config;
try
{
    config = BotConfig.FromEnvironment(Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var log = app.Logger;
var stopping = app.Lifetime.ApplicationStopping;

var slack = new SlackApi(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, config.SlackBotToken);
// 1 ターンが数分かかることもある。ストリーム全体に効くのでゆるめに
var claude = new ClaudeClient(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }, config.Claude);

AuthTestResponse me;
try
{
    me = await slack.AuthTest(stopping);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Slack の auth.test に失敗しました。SLACK_BOT_TOKEN を確認してください: {ex.Message}");
    return 1;
}
log.LogInformation("Slack にログイン: bot_user={User} bot_id={Bot} model={Model} effort={Effort} fallbacks={Fallbacks}",
    me.UserId, me.BotId, config.Claude.Model, config.Claude.Effort ?? "(既定)", config.Claude.Fallbacks);

var handler = new BotHandler(slack, claude, me.UserId!, me.BotId, config.MaxHistory, log);
var socket = new SocketModeClient(slack, config.SlackAppToken, handler.HandleAsync, log);

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => socket.Connected ? Results.Text("ok") : Results.StatusCode(503));

var socketLoop = socket.RunAsync(stopping);
app.Run();
await socketLoop;
return 0;
