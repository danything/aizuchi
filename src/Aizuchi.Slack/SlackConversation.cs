using Aizuchi.Core;

namespace Aizuchi.Slack;

/// <summary>Slack のスレッド(または DM)1 本。履歴は取得済みのものを渡してもらう</summary>
public sealed class SlackConversation(
    SlackApi api,
    string channel,
    string? threadTs,
    IReadOnlyList<SlackMessage> history,
    SlackEvent trigger,
    string botUserId,
    string? botId) : IConversation
{
    public Task<IReadOnlyList<ChatMessage>> HistoryAsync(int maxMessages, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(
            ConversationBuilder.Build(history, botUserId, botId, trigger, maxMessages));

    public async Task<IReplyDraft> BeginReplyAsync(CancellationToken ct)
    {
        var ts = await api.PostMessage(channel, ConversationBuilder.Placeholder, threadTs, ct);
        return new SlackReplyDraft(api, channel, ts, threadTs);
    }
}

/// <summary>chat.update で書き換えていく 1 メッセージ。Markdown → mrkdwn はここで</summary>
public sealed class SlackReplyDraft(SlackApi api, string channel, string ts, string? threadTs) : IReplyDraft
{
    private const string Cursor = " ▍";

    public async Task UpdateAsync(string markdown, CancellationToken ct)
    {
        // 途中経過は先頭 1 メッセージ分だけ見せる
        var partial = SlackText.Split(Mrkdwn.FromMarkdown(markdown))[0];
        try { await api.UpdateMessage(channel, ts, partial + Cursor, ct); }
        catch (SlackApiException) { /* 途中経過の失敗は無視。最終更新で取り戻す */ }
    }

    /// <summary>確定本文で置き換える。長ければ続きをスレッドに追加投稿する</summary>
    public async Task FinishAsync(string markdown, CancellationToken ct)
    {
        var parts = SlackText.Split(Mrkdwn.FromMarkdown(markdown));
        await api.UpdateMessage(channel, ts, parts[0], ct);
        foreach (var p in parts.Skip(1))
            await api.PostMessage(channel, p, threadTs, ct);
    }

    public Task FailAsync(string reason, CancellationToken ct) =>
        api.UpdateMessage(channel, ts, $":warning: 応答に失敗しました ({reason})。ログを確認してください。", ct);
}
