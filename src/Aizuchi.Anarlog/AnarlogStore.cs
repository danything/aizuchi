using System.Security.Cryptography;
using System.Text.Json;

namespace Aizuchi.Anarlog;

/// <summary>1 台のデスクトップアプリに対応する登録。Anarlog は端点ごとに別の鍵を出す</summary>
public sealed class Registration
{
    public required string Id { get; set; }
    public required string Secret { get; set; }
    /// <summary>登録した Slack ユーザー。自分の登録だけ消せる</summary>
    public required string SlackUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 署名鍵の置き場。LLM の記憶とは別のファイルに持ち、記憶にも会話文脈にも入れない。
/// 書き込みは一時ファイルに書いてから差し替える(途中で落ちても壊れない)。
/// </summary>
public sealed class AnarlogStore(string dir)
{
    private readonly string _path = Path.Combine(dir, "registrations.json");
    private readonly Lock _lock = new();
    private List<Registration>? _cache;

    public IReadOnlyList<Registration> All()
    {
        lock (_lock) return [.. Load()];
    }

    public Registration Add(string secret, string slackUser)
    {
        lock (_lock)
        {
            var list = Load();
            var reg = new Registration
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)),
                Secret = secret,
                SlackUser = slackUser,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            list.Add(reg);
            Save(list);
            return reg;
        }
    }

    /// <returns>消せたら true。他人の登録や無い ID なら false</returns>
    public bool Remove(string id, string slackUser)
    {
        lock (_lock)
        {
            var list = Load();
            var n = list.RemoveAll(r => r.Id == id && r.SlackUser == slackUser);
            if (n > 0) Save(list);
            return n > 0;
        }
    }

    private List<Registration> Load()
    {
        if (_cache is not null) return _cache;
        _cache = File.Exists(_path)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), AnarlogJson.Default.ListRegistration) ?? []
            : [];
        return _cache;
    }

    private void Save(List<Registration> list)
    {
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(list, AnarlogJson.Default.ListRegistration));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
        _cache = list;
    }
}
