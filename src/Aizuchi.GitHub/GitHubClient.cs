using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aizuchi.GitHub;

/// <summary>REST API v3 の薄い皮。owner の許可判定とトークン付与、エラーの日本語化</summary>
public sealed class GitHubClient(HttpClient http, IGitHubAuth auth)
{
    public const string ApiBase = "https://api.github.com";
    /// <summary>レート上限で待ち直す回数と、待てる長さ。検索は 1 分あたり 10 回なので数十秒待てば大抵抜ける</summary>
    private const int MaxRetries = 2;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    public static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.Ordinal) ? path : ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("aizuchi", "1.0"));
        return req;
    }

    public Task<IReadOnlyDictionary<string, string>> OwnersAsync(CancellationToken ct) => auth.OwnersAsync(ct);

    /// <summary>許可された owner か。違えば理由付きで投げる</summary>
    public async Task<string> CheckOwnerAsync(string owner, CancellationToken ct)
    {
        var owners = await auth.OwnersAsync(ct);
        var key = owner.ToLowerInvariant();
        if (!owners.ContainsKey(key))
            throw new GitHubException($"{owner} は読める範囲にありません。読めるのは: {string.Join(", ", owners.Keys)}");
        return key;
    }

    public async Task<T> GetAsync<T>(string owner, string path, JsonTypeInfo<T> info, CancellationToken ct)
    {
        var key = await CheckOwnerAsync(owner, ct);
        for (var attempt = 0; ; attempt++)
        {
            var token = await auth.TokenForAsync(key, ct);
            using var req = Request(HttpMethod.Get, path, token);
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubException($"見つかりません: {path}(リポジトリ名やパスを確認。非公開なら App のアクセス範囲も)");
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (RateLimitWait(res, DateTimeOffset.UtcNow) is not { } wait)
                    throw new GitHubException($"権限がありません (HTTP {(int)res.StatusCode}): {Trim(await res.Content.ReadAsStringAsync(ct))}");
                // 短い待ちなら黙って待って投げ直す。LLM に「時間を置いて」と言わせるより速い
                if (attempt < MaxRetries && wait <= MaxWait)
                {
                    await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                throw new GitHubException(
                    $"GitHub API のレート上限に当たりました(回復まで約 {(int)wait.TotalSeconds} 秒)。範囲を絞って調べ直してください");
            }
            if (!res.IsSuccessStatusCode)
                throw new GitHubException($"GitHub API が HTTP {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
            return await res.Content.ReadFromJsonAsync(info, ct) ?? throw new GitHubException("空の応答");
        }
    }

    /// <summary>
    /// 403 / 429 がレート上限なら、回復までの待ち時間。ただの権限不足なら null。
    /// 二次上限(検索の投げすぎなど)は Retry-After、一次上限は X-RateLimit-Remaining=0 と Reset(epoch 秒)で来る。
    /// </summary>
    public static TimeSpan? RateLimitWait(HttpResponseMessage res, DateTimeOffset now)
    {
        if (res.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } d) return NonNegative(d);
            if (retry.Date is { } at) return NonNegative(at - now);
        }
        if (Header(res, "X-RateLimit-Remaining") != "0") return null;
        return long.TryParse(Header(res, "X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset)
            ? NonNegative(DateTimeOffset.FromUnixTimeSeconds(reset) - now)
            : TimeSpan.Zero;
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static TimeSpan NonNegative(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
