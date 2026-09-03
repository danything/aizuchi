
namespace Aizuchi.Slack;

public enum Decision
{
    /// <summary>無視</summary>
    Ignore,
    /// <summary>返信する</summary>
    Reply,
    /// <summary>ボットが既に参加しているスレッドなら返信する(履歴を見て判定)</summary>
    ReplyIfBotInThread,
}

/// <summary>どのイベントに反応するかの判定。副作用なし</summary>
public static class Dispatch
{
    public static Decision Decide(SlackEvent ev, string botUserId)
    {
        if (ev.Type is not ("message" or "app_mention")) return Decision.Ignore;
        // message_changed / message_deleted / bot_message / channel_join など編集・システム系は見ない
        if (ev.Subtype is not null) return Decision.Ignore;
        // 自分と他のボットには反応しない(無限ループ防止)
        if (ev.BotId is not null || ev.User is null || ev.User == botUserId) return Decision.Ignore;
        if (string.IsNullOrWhiteSpace(ev.Text) || ev.Channel is null || ev.Ts is null) return Decision.Ignore;

        if (ev.ChannelType == "im") return Decision.Reply;
        if (SlackText.MentionsBot(ev.Text, botUserId)) return Decision.Reply;
        if (ev.ThreadTs is not null && ev.ThreadTs != ev.Ts) return Decision.ReplyIfBotInThread;
        return Decision.Ignore;
    }
}
