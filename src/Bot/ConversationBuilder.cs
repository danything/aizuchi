using SlackClaudeBot.Claude;
using SlackClaudeBot.Slack;

namespace SlackClaudeBot.Bot;

/// <summary>Slack のスレッド履歴を Claude の messages に組み直す</summary>
public static class ConversationBuilder
{
    /// <summary>返信中の仮メッセージ。履歴に混ざっていたら捨てる</summary>
    public const string Placeholder = "…";

    public static List<ChatMessage> Build(
        IReadOnlyList<SlackMessage> history,
        string botUserId,
        string? botId,
        SlackEvent trigger,
        int maxMessages)
    {
        var list = new List<ChatMessage>();
        foreach (var m in history)
        {
            // 参加通知・編集などのシステム系は捨てる。他ボットの発言(bot_message)は user として残す
            if (m.Subtype is not null and not ("bot_message" or "thread_broadcast")) continue;

            var isMe = m.User == botUserId || (botId is not null && m.BotId == botId);
            var text = isMe ? m.Text?.Trim() ?? "" : SlackText.StripMention(m.Text, botUserId);
            if (text.Length == 0 || (isMe && text == Placeholder)) continue;

            list.Add(new ChatMessage(isMe ? "assistant" : "user", text));
        }

        // 発火元のメッセージが履歴にまだ載っていないことがある
        if (!history.Any(m => m.Ts == trigger.Ts))
        {
            var text = SlackText.StripMention(trigger.Text, botUserId);
            if (text.Length > 0) list.Add(new ChatMessage("user", text));
        }

        // 先頭・末尾は user でなければならない
        while (list.Count > 0 && list[0].Role == "assistant") list.RemoveAt(0);
        while (list.Count > 0 && list[^1].Role == "assistant") list.RemoveAt(list.Count - 1);

        // 同じロールの連続は 1 つに畳む
        var merged = new List<ChatMessage>();
        foreach (var m in list)
        {
            if (merged.Count > 0 && merged[^1].Role == m.Role)
                merged[^1] = merged[^1] with { Content = merged[^1].Content + "\n\n" + m.Content };
            else
                merged.Add(m);
        }

        // 直近 maxMessages 件だけ。切った結果 assistant 始まりになったらさらに削る
        if (merged.Count > maxMessages) merged.RemoveRange(0, merged.Count - maxMessages);
        while (merged.Count > 0 && merged[0].Role == "assistant") merged.RemoveAt(0);

        return merged;
    }
}
