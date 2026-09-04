
namespace Aizuchi.Slack;

public enum Decision
{
    /// <summary>無視</summary>
    Ignore,
    /// <summary>返信する</summary>
    Reply,
    /// <summary>ボットを呼んで始まったスレッドなら返信する(親の発言を見て判定)</summary>
    ReplyIfOwnThread,
}

/// <summary>どのイベントに反応するかの判定。副作用なし</summary>
public static class Dispatch
{
    /// <param name="threadFollowUp">
    /// ボットを呼んで始まったスレッドの続きに、メンション無しでも返すか。
    /// false なら DM とメンションだけに反応する
    /// </param>
    public static Decision Decide(SlackEvent ev, string botUserId, bool threadFollowUp)
    {
        if (ev.Type is not ("message" or "app_mention")) return Decision.Ignore;
        // message_changed / message_deleted / bot_message / channel_join など編集・システム系は見ない
        if (ev.Subtype is not null) return Decision.Ignore;
        // 自分と他のボットには反応しない(無限ループ防止)
        if (ev.BotId is not null || ev.User is null || ev.User == botUserId) return Decision.Ignore;
        if (string.IsNullOrWhiteSpace(ev.Text) || ev.Channel is null || ev.Ts is null) return Decision.Ignore;

        if (ev.ChannelType == "im") return Decision.Reply;
        if (SlackText.MentionsBot(ev.Text, botUserId)) return Decision.Reply;
        if (threadFollowUp && ev.ThreadTs is not null && ev.ThreadTs != ev.Ts) return Decision.ReplyIfOwnThread;
        return Decision.Ignore;
    }

    /// <summary>
    /// スレッドの親がボットを呼んでいるか。ReplyIfOwnThread の答え合わせに使う。
    /// conversations.replies は親を先頭に古い順で返すが、念のため ts で引く。
    /// </summary>
    public static bool StartedByMention(IReadOnlyList<SlackMessage> thread, string? threadTs, string botUserId)
    {
        var parent = thread.FirstOrDefault(m => m.Ts == threadTs) ?? thread.FirstOrDefault();
        return SlackText.MentionsBot(parent?.Text, botUserId);
    }
}
