using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Aizuchi.Core;

namespace Aizuchi.OpenProject;

public sealed class OpenProjectException(string message) : Exception(message);

/// <param name="Url">セルフホストの基点。例 https://openproject.example.com(末尾のスラッシュは落とす)</param>
public sealed record OpenProjectOptions(string Url, string ApiKey)
{
    /// <summary>URL と API キーが揃っていれば有効。どちらも無ければ null(OpenProject 無効)</summary>
    public static OpenProjectOptions? FromEnvironment(Func<string, string?> env)
    {
        var url = Env.Optional(env, "OPENPROJECT_URL");
        var key = Env.Optional(env, "OPENPROJECT_API_KEY");
        if (url is null && key is null) return null;
        if (url is null || key is null)
            throw new ConfigException("OpenProject には OPENPROJECT_URL と OPENPROJECT_API_KEY の両方が要ります");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ConfigException($"OPENPROJECT_URL は http(s) の絶対 URL で指定してください: {url}");
        return new OpenProjectOptions(url.TrimEnd('/'), key);
    }
}

/// <summary>API v3 の薄い皮。API キーは Basic 認証のパスワード側に入れる(ユーザー名は apikey 固定)</summary>
public sealed class OpenProjectClient(HttpClient http, OpenProjectOptions opt)
{
    public string Url => opt.Url;

    private AuthenticationHeaderValue Auth =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:" + opt.ApiKey)));

    /// <summary>起動時の疎通確認。API キーが通らなければここで落とす</summary>
    public Task<Me> MeAsync(CancellationToken ct) => GetAsync("/users/me", OpenProjectJson.Default.Me, ct);

    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> info, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, opt.Url + "/api/v3" + path);
        req.Headers.Authorization = Auth;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
            throw new OpenProjectException("OpenProject の API キーが通りません(OPENPROJECT_API_KEY を確認)");
        if (res.StatusCode is HttpStatusCode.Forbidden)
            throw new OpenProjectException("この API キーでは見られません(ユーザーの権限を確認)");
        if (res.StatusCode is HttpStatusCode.NotFound)
            throw new OpenProjectException($"見つかりません: {path}(ID や識別子を確認)");
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new OpenProjectException($"OpenProject API が HTTP {(int)res.StatusCode}: {Format.Cap(body, 300)}");
        }
        return await res.Content.ReadFromJsonAsync(info, ct) ?? throw new OpenProjectException("空の応答");
    }
}
