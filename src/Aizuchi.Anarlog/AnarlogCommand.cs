using System.Text;
using System.Text.RegularExpressions;
using Aizuchi.Core;

namespace Aizuchi.Anarlog;

/// <summary>
/// 「anarlog add whsec_…」などの手動コマンド。LLM を通さず、鍵を文脈に入れない。
/// add は DM でしか受け付けない(チャンネルに鍵を貼らせない)。
/// </summary>
public sealed partial class AnarlogCommand(AnarlogStore store, AnarlogOptions opt) : IChatCommand
{
    [GeneratedRegex(@"^\s*anarlog(?:\s+(?<verb>\S+))?(?:\s+(?<arg>\S+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^whsec_[0-9a-f]{8,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretShape();

    public Task<string?> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var m = Pattern().Match(message.Text);
        if (!m.Success) return Task.FromResult<string?>(null);
        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var arg = m.Groups["arg"].Value;
        var user = message.UserId ?? "";

        return Task.FromResult<string?>(verb switch
        {
            "" or "list" or "help" => List(user),
            "add" => Add(arg, user, message.IsDirect),
            "remove" or "rm" or "delete" => Remove(arg, user),
            _ => "知らない操作です。\n" + Usage(),
        });
    }

    private string List(string user)
    {
        var mine = store.All().Where(r => r.SlackUser == user).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(mine.Count == 0
            ? "あなたの登録はありません。"
            : $"あなたの登録 ({mine.Count} 件):");
        foreach (var r in mine)
            sb.AppendLine($"- `{r.Id}` 登録 {r.CreatedAt.ToLocalTime():yyyy-MM-dd}");
        sb.AppendLine();
        sb.Append(Usage());
        return sb.ToString();
    }

    private string Add(string secret, string user, bool isDirect)
    {
        if (!isDirect)
            return "鍵はチャンネルに貼らず、私への DM で `anarlog add whsec_…` と送ってください。" +
                   "この発言は削除しておくことをおすすめします。";
        if (string.IsNullOrEmpty(user)) return "誰の登録か分からないので受け付けられません。";
        if (!SecretShape().IsMatch(secret))
            return "鍵の形が違います。Anarlog が端点作成時に表示する `whsec_` で始まる文字列をそのまま送ってください。";
        var reg = store.Add(secret, user);
        return $"登録しました(ID `{reg.Id}`)。Anarlog の Webhooks 画面で **Test** を押すと、" +
               $"<#{opt.Channel}> に確認のメッセージが届きます。\n" +
               "この DM の鍵は保存済みなので、メッセージは削除して構いません。";
    }

    private string Remove(string id, string user)
    {
        if (string.IsNullOrEmpty(id)) return "消す登録の ID を指定してください。`anarlog list` で確認できます。";
        return store.Remove(id, user)
            ? $"登録 `{id}` を消しました。Anarlog 側の端点も削除しておいてください。"
            : $"`{id}` はあなたの登録にありません。`anarlog list` で確認してください。";
    }

    private string Usage()
    {
        var url = opt.PublicUrl ?? "https://<受け口のホスト>/webhooks/anarlog";
        return $"""
            **Anarlog の会議要約を Slack に流す手順**
            1. Anarlog の Settings → Developers → Webhooks で `{url}` を追加
            2. 表示された `whsec_…` を、私への DM で `anarlog add whsec_…` と送る(一度しか表示されません)
            3. Anarlog で Test を押して、<#{opt.Channel}> に届くのを確認

            `anarlog list` 自分の登録を見る / `anarlog remove <ID>` 登録を消す
            デスクトップアプリごとに登録が要ります(配信はアプリを開いている間だけ)。
            """;
    }
}
