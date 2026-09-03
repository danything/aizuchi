using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aizuchi.GitHub;

/// <summary>REST API v3 の薄い皮。owner の許可判定とトークン付与、エラーの日本語化</summary>
public sealed class GitHubClient(HttpClient http, IGitHubAuth auth)
{
    public const string ApiBase = "https://api.github.com";

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
        var token = await auth.TokenForAsync(key, ct);
        using var req = Request(HttpMethod.Get, path, token);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubException($"見つかりません: {path}(リポジトリ名やパスを確認。非公開なら App のアクセス範囲も)");
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var remaining = res.Headers.TryGetValues("X-RateLimit-Remaining", out var v) ? v.FirstOrDefault() : null;
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new GitHubException(remaining == "0" ? "GitHub API のレート上限に当たりました。しばらく待ってください" : $"権限がありません (HTTP 403): {Trim(body)}");
        }
        if (!res.IsSuccessStatusCode)
            throw new GitHubException($"GitHub API が HTTP {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
        return await res.Content.ReadFromJsonAsync(info, ct) ?? throw new GitHubException("空の応答");
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
