using SlackClaudeBot.Slack;

namespace SlackClaudeBot.Bot;

/// <summary>
/// Claude のストリームを Slack の 1 メッセージに間引きながら流し込む。
/// chat.update は Tier 3(50+/分)なので 1.5 秒間隔で十分速く、制限にも当たらない。
/// </summary>
public sealed class SlackStreamer(SlackApi slack, string channel, string ts, TimeSpan interval)
{
    private const string Cursor = " ▍";
    private readonly System.Text.StringBuilder _buffer = new();
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

    public async Task Append(string delta, CancellationToken ct)
    {
        _buffer.Append(delta);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastUpdate < interval) return;
        _lastUpdate = now;
        // 途中経過は先頭 1 メッセージ分だけ見せる
        var partial = SlackText.Split(Mrkdwn.FromMarkdown(_buffer.ToString()))[0];
        try { await slack.UpdateMessage(channel, ts, partial + Cursor, ct); }
        catch (SlackApiException) { /* 途中経過の失敗は無視。最終更新で取り戻す */ }
    }

    /// <summary>確定した本文で置き換える。長ければ続きをスレッドに追加投稿する</summary>
    public async Task Finish(string finalText, string? threadTs, CancellationToken ct)
    {
        var parts = SlackText.Split(finalText);
        await slack.UpdateMessage(channel, ts, parts[0], ct);
        foreach (var p in parts.Skip(1))
            await slack.PostMessage(channel, p, threadTs, ct);
    }
}
