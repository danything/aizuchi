using Aizuchi.Core;

namespace Aizuchi.Slack;

public sealed record SlackOptions(string BotToken, string AppToken)
{
    public static SlackOptions FromEnvironment(Func<string, string?> env) => new(
        BotToken: Env.Required(env, "SLACK_BOT_TOKEN"),
        AppToken: Env.Required(env, "SLACK_APP_TOKEN"));
}
