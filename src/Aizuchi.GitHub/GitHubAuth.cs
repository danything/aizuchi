using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aizuchi.Core;

namespace Aizuchi.GitHub;

/// <summary>owner ごとのトークンを出す。App なら installation 単位、PAT なら 1 本</summary>
public interface IGitHubAuth
{
    /// <summary>読める owner(小文字)→ 種別("Organization" / "User")</summary>
    Task<IReadOnlyDictionary<string, string>> OwnersAsync(CancellationToken ct);

    Task<string> TokenForAsync(string owner, CancellationToken ct);
}

public sealed record GitHubOptions(string? Token, string? AppId, string? PrivateKeyPem, IReadOnlyList<string> Owners)
{
    /// <summary>GITHUB_TOKEN があれば PAT、無ければ GITHUB_APP_ID + GITHUB_APP_PRIVATE_KEY。どちらも無ければ null(GitHub 無効)</summary>
    public static GitHubOptions? FromEnvironment(Func<string, string?> env)
    {
        var token = Env.Optional(env, "GITHUB_TOKEN");
        var appId = Env.Optional(env, "GITHUB_APP_ID");
        var pem = env("GITHUB_APP_PRIVATE_KEY");
        var owners = (Env.Optional(env, "GITHUB_OWNERS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.ToLowerInvariant()).ToList();

        if (token is null && appId is null && string.IsNullOrWhiteSpace(pem)) return null;
        if (token is null && (appId is null || string.IsNullOrWhiteSpace(pem)))
            throw new ConfigException("GitHub App には GITHUB_APP_ID と GITHUB_APP_PRIVATE_KEY の両方が要ります(PAT なら GITHUB_TOKEN)");
        if (token is not null && owners.Count == 0)
            throw new ConfigException("GITHUB_TOKEN(PAT)のときは読ませる owner を GITHUB_OWNERS に列挙してください(例: danything,5ym)");
        return new GitHubOptions(token, appId, pem, owners);
    }
}

/// <summary>PAT。owner は設定で決め打ち、種別は /users/{owner} で引く</summary>
public sealed class TokenAuth(HttpClient http, string token, IReadOnlyList<string> owners) : IGitHubAuth
{
    private Dictionary<string, string>? _owners;

    public async Task<IReadOnlyDictionary<string, string>> OwnersAsync(CancellationToken ct)
    {
        if (_owners is not null) return _owners;
        var map = new Dictionary<string, string>();
        foreach (var o in owners)
        {
            using var req = GitHubClient.Request(HttpMethod.Get, $"/users/{o}", token);
            using var res = await http.SendAsync(req, ct);
            var acc = res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync(GitHubJson.Default.Account, ct) : null;
            map[o] = acc?.Type ?? "User";
        }
        return _owners = map;
    }

    public Task<string> TokenForAsync(string owner, CancellationToken ct) => Task.FromResult(token);
}

/// <summary>
/// GitHub App。秘密鍵で JWT を作り、/app/installations で入っている先を拾い、
/// owner ごとに installation token(1 時間)を取って使い回す。
/// </summary>
public sealed class AppAuth(HttpClient http, string appId, string privateKeyPem, IReadOnlyList<string> restrictTo, TimeProvider? clock = null) : IGitHubAuth
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, (long InstallationId, string Type)>? _installations;
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public async Task<IReadOnlyDictionary<string, string>> OwnersAsync(CancellationToken ct)
    {
        var inst = await Installations(ct);
        return inst.ToDictionary(kv => kv.Key, kv => kv.Value.Type);
    }

    public async Task<string> TokenForAsync(string owner, CancellationToken ct)
    {
        owner = owner.ToLowerInvariant();
        var inst = await Installations(ct);
        if (!inst.TryGetValue(owner, out var i))
            throw new GitHubException($"{owner} には GitHub App がインストールされていません");

        await _lock.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(owner, out var t) && t.ExpiresAt - _clock.GetUtcNow() > TimeSpan.FromMinutes(5))
                return t.Token;
            using var req = GitHubClient.Request(HttpMethod.Post, $"/app/installations/{i.InstallationId}/access_tokens", Jwt());
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                throw new GitHubException($"installation token の取得に失敗 (HTTP {(int)res.StatusCode}): {await res.Content.ReadAsStringAsync(ct)}");
            var body = await res.Content.ReadFromJsonAsync(GitHubJson.Default.InstallationToken, ct);
            if (body?.Token is null) throw new GitHubException("installation token が空");
            _tokens[owner] = (body.Token, body.ExpiresAt ?? _clock.GetUtcNow().AddMinutes(55));
            return body.Token;
        }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, (long InstallationId, string Type)>> Installations(CancellationToken ct)
    {
        if (_installations is not null) return _installations;
        using var req = GitHubClient.Request(HttpMethod.Get, "/app/installations?per_page=100", Jwt());
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new GitHubException($"/app/installations に失敗 (HTTP {(int)res.StatusCode}): {await res.Content.ReadAsStringAsync(ct)}");
        var list = await res.Content.ReadFromJsonAsync(GitHubJson.Default.ListInstallation, ct) ?? [];
        var map = new Dictionary<string, (long InstallationId, string Type)>();
        foreach (var i in list)
        {
            var login = i.Account?.Login?.ToLowerInvariant();
            if (login is null) continue;
            if (restrictTo.Count > 0 && !restrictTo.Contains(login)) continue;
            map[login] = (i.Id, i.Account?.Type ?? "User");
        }
        return _installations = map;
    }

    /// <summary>App 認証用の JWT。iat は時計ずれ対策で 60 秒戻し、有効期限は 9 分</summary>
    public string Jwt() => GitHubJwt.Create(appId, privateKeyPem, _clock.GetUtcNow());
}

public static class GitHubJwt
{
    public static string Create(string appId, string privateKeyPem, DateTimeOffset now)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new JwtHeader(), GitHubJson.Default.JwtHeader);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new JwtPayload
        {
            Iat = now.ToUnixTimeSeconds() - 60,
            Exp = now.ToUnixTimeSeconds() + 9 * 60,
            Iss = appId,
        }, GitHubJson.Default.JwtPayload);
        var signingInput = Base64Url(header) + "." + Base64Url(payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GitHubException(string message) : Exception(message);
