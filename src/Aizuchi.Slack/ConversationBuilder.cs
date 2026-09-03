using Aizuchi.Core;

namespace Aizuchi.Slack;

/// <summary>Slack のスレッド履歴を Claude の messages に組み直す</summary>
public static class ConversationBuilder
{
    /// <summary>返信中の仮メッセージ。履歴に混ざっていたら捨てる</summary>
    public const string Placeholder = "…";

    /// <param name="names">ユーザー ID → 表示名。複数人のスレッドでは user 発言に [名前] を前置する</param>
    /// <param name="preamble">最初の user 発言の前に置く参考情報(チャンネルの直近の流れなど)</param>
    public static List<ChatMessage> Build(
        IReadOnlyList<SlackMessage> history,
        string botUserId,
        string? botId,
        SlackEvent trigger,
        int maxMessages,
        IReadOnlyDictionary<string, string>? names = null,
        string? preamble = null)
    {
        var humans = history.Where(m => m.User is not null && m.User != botUserId && m.Subtype is null)
            .Select(m => m.User!).Distinct().Count();
        var labelSpeakers = humans >= 2 && names is not null;

        var list = new List<ChatMessage>();
        foreach (var m in history)
        {
            // 参加通知・編集などのシステム系は捨てる。他ボットの発言(bot_message)は user として残す
            if (m.Subtype is not null and not ("bot_message" or "thread_broadcast")) continue;

            var isMe = m.User == botUserId || (botId is not null && m.BotId == botId);
            var text = isMe ? m.Text?.Trim() ?? "" : SlackText.StripMention(m.Text, botUserId);
            if (text.Length == 0 || (isMe && text == Placeholder)) continue;
            if (!isMe && labelSpeakers && m.User is { } uid)
                text = $"[{(names!.TryGetValue(uid, out var n) ? n : uid)}] {text}";

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

        if (merged.Count > 0 && !string.IsNullOrWhiteSpace(preamble))
            merged[0] = merged[0] with { Content = preamble.Trim() + "\n\n---\n\n" + merged[0].Content };

        return merged;
    }

    /// <summary>チャンネルの直近の流れを参考情報にする。スレッドの親は本文に出るので除く</summary>
    public static string? ChannelContext(
        IReadOnlyList<SlackMessage> recent,
        string? excludeTs,
        string botUserId,
        string? botId,
        IReadOnlyDictionary<string, string> names,
        int max)
    {
        var lines = recent
            .Where(m => m.Ts != excludeTs && m.Subtype is null or "bot_message" && !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(max)
            .Select(m =>
            {
                var isMe = m.User == botUserId || (botId is not null && m.BotId == botId);
                var who = isMe ? "aizuchi" : m.User is { } u && names.TryGetValue(u, out var n) ? n : m.User ?? "?";
                return $"[{who}] {SlackText.StripMention(m.Text, botUserId)}";
            })
            .ToList();
        if (lines.Count == 0) return null;
        return "(参考: このチャンネルの直近のやりとり。スレッドの外)\n" + string.Join("\n", lines);
    }
}
