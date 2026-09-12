using Microsoft.Extensions.Logging;

namespace Aizuchi.Core;

/// <summary>
/// /webhooks/{name} の本体。ホストは本文を読んでここに渡すだけ。
/// 投稿に失敗したら 503 を返して送り元の再送に任せる(こちらで溜めない)。
/// Anarlog は非 2xx なら 5 秒・30 秒後に再送するので、これで十分。
/// </summary>
public sealed class WebhookEndpoint(IReadOnlyList<IWebhookSource> sources, IChannelPoster poster, ILogger log)
{
    /// <summary>本文の上限。会議の全文文字起こしが入るので少し余裕を持たせる</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    public sealed record Reply(int Status, string Text);

    public async Task<Reply> HandleAsync(string name, WebhookRequest request, CancellationToken ct)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (source is null) return new(404, "unknown source");
        if (request.Body.Length > MaxBodyBytes) return new(413, "body too large");

        switch (await source.HandleAsync(request, ct))
        {
            case WebhookOutcome.Reject r:
                log.LogWarning("webhook 拒否 source={Source} status={Status}: {Reason}", name, r.Status, r.Reason);
                return new(r.Status, r.Reason);
            case WebhookOutcome.Ignore i:
                log.LogInformation("webhook 受信 source={Source} 投稿なし: {Reason}", name, i.Reason);
                return new(200, "ok");
            case WebhookOutcome.Post p:
                try
                {
                    await poster.PostAsync(p.Channel, p.Markdown, ct);
                    log.LogInformation("webhook 受信 source={Source} channel={Channel} に投稿", name, p.Channel);
                    return new(200, "ok");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "webhook の投稿に失敗 source={Source} channel={Channel}。送り元の再送に任せる", name, p.Channel);
                    return new(503, "post failed");
                }
            default:
                return new(500, "unexpected outcome");
        }
    }
}
