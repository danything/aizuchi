using Aizuchi.Core;

namespace Aizuchi.Slack;

/// <param name="ThreadFollowUp">
/// ボットを呼んで始まったスレッドの続きに、メンション無しでも返すか。
/// 既定は on。off にすると DM 以外はいつでもメンションが要る
/// </param>
public sealed record SlackOptions(string BotToken, string AppToken, bool ThreadFollowUp)
{
    public static SlackOptions FromEnvironment(Func<string, string?> env) => new(
        BotToken: Env.Required(env, "SLACK_BOT_TOKEN"),
        AppToken: Env.Required(env, "SLACK_APP_TOKEN"),
        ThreadFollowUp: !string.Equals(Env.Optional(env, "SLACK_THREAD_FOLLOWUP"), "off", StringComparison.OrdinalIgnoreCase));
}
