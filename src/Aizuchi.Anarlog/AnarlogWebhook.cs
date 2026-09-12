using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aizuchi.Core;

namespace Aizuchi.Anarlog;

/// <param name="Channel">投稿先の Slack チャンネル ID。ボットを招待しておくこと</param>
/// <param name="StoreDir">署名鍵の置き場。デスクトップアプリごとに 1 本ずつ登録される</param>
/// <param name="PublicUrl">Anarlog に登録してもらう URL。案内文に使うだけ</param>
public sealed record AnarlogOptions(string Channel, string StoreDir, string? PublicUrl)
{
    /// <summary>ANARLOG_SLACK_CHANNEL があれば有効。鍵は Slack から登録するので環境変数には持たない</summary>
    public static AnarlogOptions? FromEnvironment(Func<string, string?> env)
    {
        var channel = Env.Optional(env, "ANARLOG_SLACK_CHANNEL");
        if (channel is null) return null;
        return new AnarlogOptions(
            channel,
            Env.Or(env, "ANARLOG_STORE_DIR", "data/anarlog"),
            Env.Optional(env, "ANARLOG_PUBLIC_URL"));
    }
}

/// <summary>
/// Anarlog の webhook(docs/reference/webhooks.mdx)。署名を確かめ、note.enhanced が来たら
/// 有料の Slack 連携と同じ形(タイトル + AI 要約)でチャンネルに流す。
/// </summary>
public sealed class AnarlogWebhook(AnarlogStore store, AnarlogOptions opt, TimeProvider? clock = null) : IWebhookSource
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    /// <summary>再送(5 秒・30 秒後)で同じ配信を二度投稿しないための記憶</summary>
    private readonly RecentKeys _seen = new(1000);
    /// <summary>x-anarlog-timestamp がこれより古いか未来なら再生とみなして捨てる</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public string Name => "anarlog";

    public Task<WebhookOutcome> HandleAsync(WebhookRequest req, CancellationToken ct) =>
        Task.FromResult(Handle(req));

    private WebhookOutcome Handle(WebhookRequest req)
    {
        // 1. 署名。本文を解釈する前に、生のバイト列に対して確かめる。
        //    鍵はデスクトップアプリごとに違うので、登録されている鍵を順に試す(数本なので十分速い)
        var signature = req.Header("x-anarlog-signature");
        var registrations = store.All();
        if (registrations.Count == 0)
            return new WebhookOutcome.Reject(401, "登録された署名鍵がありません(DM で anarlog add を)");
        if (!registrations.Any(r => Signature.Verify(r.Secret, req.Body.Span, signature)))
            return new WebhookOutcome.Reject(401, "署名がどの登録とも一致しません");
        // 2. 再生対策。署名が正しくても古い配信は捨てる
        if (!Fresh(req.Header("x-anarlog-timestamp")))
            return new WebhookOutcome.Reject(401, "タイムスタンプが古いか未来です");

        Envelope? env;
        try { env = JsonSerializer.Deserialize(req.Body.Span, AnarlogJson.Default.Envelope); }
        catch (JsonException) { return new WebhookOutcome.Reject(400, "JSON を読めません"); }
        if (env?.Id is not { Length: > 0 } || env.Event is null)
            return new WebhookOutcome.Reject(400, "id か event がありません");

        // 3. 再送で二重投稿しない。id は配信ごとではなくイベントごとに同じ
        if (!_seen.Add(env.Id)) return new WebhookOutcome.Ignore($"処理済み {env.Id}");

        return env.Event switch
        {
            "webhook.test" => new WebhookOutcome.Post(opt.Channel, "✅ Anarlog からのテスト配信を受け取りました。この経路で会議の要約が届きます。"),
            "note.enhanced" => Recap.Render(env) is { } md
                ? new WebhookOutcome.Post(opt.Channel, md)
                : new WebhookOutcome.Ignore("要約も本文も無い"),
            // 録音が終わった時点では要約がまだ無い。有料の Slack 連携も note.enhanced で動く
            "meeting.completed" => new WebhookOutcome.Ignore("meeting.completed は投稿しない(要約待ち)"),
            _ => new WebhookOutcome.Ignore($"未知のイベント {env.Event}"),
        };
    }

    private bool Fresh(string? header)
    {
        if (!long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) return false;
        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
        var skew = _clock.GetUtcNow() - sent;
        return skew > -MaxSkew && skew < MaxSkew;
    }
}

/// <summary>x-anarlog-signature = "sha256=" + hex(HMAC-SHA256(secret, 生の本文))</summary>
public static class Signature
{
    public static string Compute(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>長さも含めて定数時間で比べる。無い・空・形が違うは全部 false</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var expected = Encoding.ASCII.GetBytes(Compute(secret, body));
        var received = Encoding.ASCII.GetBytes(header.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}

/// <summary>投稿文の組み立て。純粋関数なのでテストしやすい</summary>
public static class Recap
{
    /// <summary>
    /// 有料の Slack 連携と同じ形: *タイトル* + 先頭の AI 要約 + 署名行。要約が無ければ手書きメモ、
    /// それも無ければ null(投稿しない)。未完了のアクションアイテムがあれば添える。
    /// </summary>
    public static string? Render(Envelope env)
    {
        var m = env.Data?.Meeting;
        if (m is null) return null;
        var body = m.Summaries?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Markdown))?.Markdown
                   ?? m.Note?.Markdown;
        if (string.IsNullOrWhiteSpace(body)) return null;

        var title = string.IsNullOrWhiteSpace(m.Title) ? "Untitled meeting" : m.Title.Trim();
        var sb = new StringBuilder();
        sb.Append("**").Append(title).Append("**\n\n").Append(body.Trim());

        var todo = (m.ActionItems ?? [])
            .Where(a => a.CompletedAt is null && !string.IsNullOrWhiteSpace(a.Text))
            .Select(a => a.Text!.Trim()).ToList();
        if (todo.Count > 0)
        {
            sb.Append("\n\n**アクションアイテム**");
            foreach (var t in todo) sb.Append("\n- ").Append(t);
        }

        var date = Date(m.StartedAt) ?? Date(m.CreatedAt);
        sb.Append("\n\n_Anarlog の会議メモ").Append(date is null ? "" : " · " + date).Append('_');
        return sb.ToString();
    }

    /// <summary>ISO 8601 の先頭 10 文字。有料版も日付はこの切り方</summary>
    private static string? Date(string? iso) => iso is { Length: >= 10 } ? iso[..10] : null;
}
