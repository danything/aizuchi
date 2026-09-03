using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace SlackClaudeBot.Slack;

public sealed class SlackApiException(string method, string? error)
    : Exception($"Slack API {method} が失敗: {error ?? "(理由不明)"}")
{
    public string Method { get; } = method;
    public string? Error { get; } = error;
}

/// <summary>Slack Web API の必要最小限。ボットトークン(xoxb-)で叩く</summary>
public sealed class SlackApi(HttpClient http, string botToken)
{
    private const string Base = "https://slack.com/api/";

    /// <summary>Socket Mode の WebSocket URL を取得する。これだけアプリレベルトークン(xapp-)</summary>
    public async Task<string> OpenSocketUrl(string appToken, CancellationToken ct)
    {
        var res = await Send(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + "apps.connections.open");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
            return req;
        }, SlackJson.Default.ConnectionsOpenResponse, ct);
        if (!res.Ok || res.Url is null) throw new SlackApiException("apps.connections.open", res.Error);
        return res.Url;
    }

    public async Task<AuthTestResponse> AuthTest(CancellationToken ct)
    {
        var res = await Send(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + "auth.test");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            return req;
        }, SlackJson.Default.AuthTestResponse, ct);
        if (!res.Ok || res.UserId is null) throw new SlackApiException("auth.test", res.Error);
        return res;
    }

    /// <returns>投稿したメッセージの ts</returns>
    public async Task<string> PostMessage(string channel, string text, string? threadTs, CancellationToken ct)
    {
        var res = await PostJson("chat.postMessage",
            new PostMessageRequest { Channel = channel, Text = text, ThreadTs = threadTs },
            SlackJson.Default.PostMessageRequest, SlackJson.Default.PostMessageResponse, ct);
        if (!res.Ok || res.Ts is null) throw new SlackApiException("chat.postMessage", res.Error);
        return res.Ts;
    }

    public async Task UpdateMessage(string channel, string ts, string text, CancellationToken ct)
    {
        var res = await PostJson("chat.update",
            new UpdateMessageRequest { Channel = channel, Ts = ts, Text = text },
            SlackJson.Default.UpdateMessageRequest, SlackJson.Default.PostMessageResponse, ct);
        if (!res.Ok) throw new SlackApiException("chat.update", res.Error);
    }

    /// <summary>スレッドの全メッセージ(親含む)。古い順</summary>
    public async Task<List<SlackMessage>> Replies(string channel, string threadTs, int limit, CancellationToken ct)
    {
        var res = await Get("conversations.replies",
            $"channel={Uri.EscapeDataString(channel)}&ts={Uri.EscapeDataString(threadTs)}&limit={limit}",
            SlackJson.Default.MessagesResponse, ct);
        if (!res.Ok) throw new SlackApiException("conversations.replies", res.Error);
        return res.Messages ?? [];
    }

    /// <summary>チャンネル直近の履歴。API は新しい順で返すので古い順に直して返す</summary>
    public async Task<List<SlackMessage>> History(string channel, int limit, CancellationToken ct)
    {
        var res = await Get("conversations.history",
            $"channel={Uri.EscapeDataString(channel)}&limit={limit}",
            SlackJson.Default.MessagesResponse, ct);
        if (!res.Ok) throw new SlackApiException("conversations.history", res.Error);
        var list = res.Messages ?? [];
        list.Reverse();
        return list;
    }

    private Task<TRes> PostJson<TReq, TRes>(string method, TReq body,
        JsonTypeInfo<TReq> reqInfo, JsonTypeInfo<TRes> resInfo, CancellationToken ct) =>
        Send(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + method)
            {
                Content = JsonContent.Create(body, reqInfo),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            return req;
        }, resInfo, ct);

    private Task<TRes> Get<TRes>(string method, string query, JsonTypeInfo<TRes> resInfo, CancellationToken ct) =>
        Send(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}{method}?{query}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            return req;
        }, resInfo, ct);

    /// <summary>
    /// 429 は Retry-After 秒待って数回まで再試行する。HttpRequestMessage は使い捨てなので
    /// 試行ごとにファクトリで作り直す。
    /// </summary>
    private async Task<TRes> Send<TRes>(Func<HttpRequestMessage> make, JsonTypeInfo<TRes> resInfo, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(wait, ct);
                continue;
            }
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync(resInfo, ct)
                   ?? throw new SlackApiException(req.RequestUri!.AbsolutePath, "空の応答");
        }
    }
}
