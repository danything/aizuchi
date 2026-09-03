using Microsoft.Extensions.Logging;
using SlackClaudeBot.Claude;
using SlackClaudeBot.Slack;

namespace SlackClaudeBot.Bot;

/// <summary>1 イベント = 1 返信の流れ。判定 → 履歴取得 → 仮投稿 → ストリーム → 確定</summary>
public sealed class BotHandler(
    SlackApi slack,
    ClaudeClient claude,
    string botUserId,
    string? botId,
    int maxHistory,
    ILogger log)
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1.5);
    private readonly RecentKeys _seen = new(2000);

    public async Task HandleAsync(SlackEvent ev, string? eventId, CancellationToken ct)
    {
        var decision = Dispatch.Decide(ev, botUserId);
        if (decision == Decision.Ignore) return;
        // app_mention と message は同じ発言で両方届く。再送も来る
        if (!_seen.Add($"{ev.Channel}:{ev.Ts}")) return;

        var channel = ev.Channel!;
        var isDm = ev.ChannelType == "im";
        // チャンネルではスレッドで返す。DM のトップレベルはそのまま返す
        var threadTs = ev.ThreadTs ?? (isDm ? null : ev.Ts);

        var history = threadTs is not null
            ? await slack.Replies(channel, threadTs, maxHistory, ct)
            : await slack.History(channel, maxHistory, ct);

        if (decision == Decision.ReplyIfBotInThread &&
            !history.Any(m => m.User == botUserId || (botId is not null && m.BotId == botId)))
            return;

        var messages = ConversationBuilder.Build(history, botUserId, botId, ev, maxHistory);
        if (messages.Count == 0) return;

        var replyTs = await slack.PostMessage(channel, ConversationBuilder.Placeholder, threadTs, ct);
        var streamer = new SlackStreamer(slack, channel, replyTs, UpdateInterval);
        try
        {
            var result = await claude.StreamAsync(messages, t => streamer.Append(t, ct), ct);
            var text = result.StopReason switch
            {
                "refusal" => ":no_entry_sign: この依頼には応答できませんでした。",
                _ => Mrkdwn.FromMarkdown(result.Text),
            };
            if (result.StopReason == "max_tokens") text += "\n\n_(出力上限に達したため途中で切れています)_";
            if (text.Length == 0) text = "_(応答が空でした)_";

            await streamer.Finish(text, threadTs, ct);
            log.LogInformation(
                "返信完了 channel={Channel} thread={Thread} model={Model} in={In} cache_read={Cache} out={Out} stop={Stop}",
                channel, threadTs ?? "-", result.Model, result.InputTokens, result.CacheReadTokens, result.OutputTokens, result.StopReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "返信に失敗 channel={Channel} ts={Ts} event_id={EventId}", channel, ev.Ts, eventId);
            var reason = ex is ClaudeApiException c ? $"Claude API HTTP {c.Status}" : ex.GetType().Name;
            try { await slack.UpdateMessage(channel, replyTs, $":warning: 応答に失敗しました ({reason})。ログを確認してください。", ct); }
            catch (Exception ex2) { log.LogWarning(ex2, "エラー表示の更新にも失敗"); }
        }
    }
}
