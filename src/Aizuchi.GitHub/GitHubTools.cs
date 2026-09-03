using System.Text;
using System.Text.Json;
using Aizuchi.Core;

namespace Aizuchi.GitHub;

/// <summary>GitHub を読む道具(読み取り専用)。結果は LLM が読みやすい短い Markdown にして返す</summary>
public sealed class GitHubToolPack : IToolPack
{
    private readonly GitHubClient _client;
    private readonly IReadOnlyDictionary<string, string> _owners;

    private GitHubToolPack(GitHubClient client, IReadOnlyDictionary<string, string> owners)
    {
        _client = client;
        _owners = owners;
        Tools =
        [
            new Tool("github_repos", "読めるリポジトリの一覧(owner で絞れる)。リポジトリ名が曖昧なときはまずこれで確かめる",
                Schema("""
                    "owner": {"type": "string", "description": "省略で全部"}
                    """, []),
                Repos),
            new Tool("github_search", "コード / Issue / PR を横断検索する。query は GitHub の検索構文(例: \"WebSocket path:src\", \"is:open label:bug\")",
                Schema("""
                    "kind": {"type": "string", "enum": ["code", "issues", "prs"]},
                    "query": {"type": "string"},
                    "owner": {"type": "string", "description": "省略で読める全 owner"},
                    "repo": {"type": "string", "description": "owner/name。指定すると 1 リポジトリに絞る"},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 30}
                    """, ["kind", "query"]),
                Search),
            new Tool("github_read_file", "リポジトリのファイルを読む。大きければ行範囲で切って返す",
                Schema("""
                    "repo": {"type": "string", "description": "owner/name"},
                    "path": {"type": "string"},
                    "ref": {"type": "string", "description": "ブランチ・タグ・SHA。省略で既定ブランチ"},
                    "start_line": {"type": "integer", "minimum": 1},
                    "end_line": {"type": "integer", "minimum": 1}
                    """, ["repo", "path"]),
                ReadFile),
            new Tool("github_list", "Issue または PR の一覧",
                Schema("""
                    "repo": {"type": "string", "description": "owner/name"},
                    "kind": {"type": "string", "enum": ["issues", "prs"]},
                    "state": {"type": "string", "enum": ["open", "closed", "all"]},
                    "labels": {"type": "string", "description": "カンマ区切り(issues のみ)"},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 30}
                    """, ["repo", "kind"]),
                List),
            new Tool("github_get", "Issue / PR の本文とコメント。PR は変更ファイル一覧も",
                Schema("""
                    "repo": {"type": "string", "description": "owner/name"},
                    "number": {"type": "integer"}
                    """, ["repo", "number"]),
                Get),
            new Tool("github_commits", "直近のコミット。ブランチやパスで絞れる",
                Schema("""
                    "repo": {"type": "string", "description": "owner/name"},
                    "ref": {"type": "string", "description": "ブランチや SHA。省略で既定ブランチ"},
                    "path": {"type": "string", "description": "このパス配下を触ったものだけ"},
                    "since": {"type": "string", "description": "ISO 8601 の日時。これ以降だけ"},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 30}
                    """, ["repo"]),
                Commits),
        ];
    }

    public static async Task<GitHubToolPack> CreateAsync(GitHubClient client, CancellationToken ct) =>
        new(client, await client.OwnersAsync(ct));

    public string Name => "github";
    public IReadOnlyList<ITool> Tools { get; }

    public string PromptSection => $"""
        # GitHub
        github_* の道具で、次の owner のリポジトリを読めます(読み取り専用): {string.Join(", ", _owners.Keys)}。
        - リポジトリ名は "owner/name" で渡す。名前が曖昧なら github_repos で確かめてから使う
        - 調べものは github_search → github_get / github_read_file の順で絞る。同じ問い合わせを繰り返さない
        - 結果は要点だけを引用し、必ずリンク(html_url)を添える
        """;

    /// <summary>結果 1 件あたりの上限。長い本文はここで切る</summary>
    private const int MaxChars = 6000;

    private static string Schema(string properties, string[] required) =>
        "{\"type\": \"object\", \"properties\": {" + properties + "}, \"required\": [" +
        string.Join(", ", required.Select(r => "\"" + r + "\"")) + "], \"additionalProperties\": false}";

    // ---- 各道具 ----

    private async Task<string> Repos(JsonElement a, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var owner in _owners.Keys.Where(o => Str(a, "owner") is not { } w || w.Equals(o, StringComparison.OrdinalIgnoreCase)))
        {
            var path = _owners[owner] == "Organization" ? $"/orgs/{owner}/repos?per_page=100&sort=pushed" : $"/users/{owner}/repos?per_page=100&sort=pushed";
            List<Repo> repos;
            try { repos = await _client.GetAsync(owner, path, GitHubJson.Default.ListRepo, ct); }
            catch (GitHubException) when (_owners[owner] != "Organization")
            {
                // User への App インストールは /users/{u}/repos で非公開が見えないので installation 側で引く
                repos = (await _client.GetAsync(owner, "/installation/repositories?per_page=100", GitHubJson.Default.InstallationRepos, ct)).Repositories ?? [];
            }
            foreach (var r in repos.Where(r => !r.Archived))
                sb.AppendLine($"- {r.FullName}{(r.Private ? " (private)" : "")}{(r.Language is { } l ? $" [{l}]" : "")}: {Format.OneLine(r.Description, 120)} (pushed {Format.Date(r.PushedAt)})");
        }
        return sb.Length == 0 ? "リポジトリが見つかりません" : sb.ToString();
    }

    private async Task<string> Search(JsonElement a, CancellationToken ct)
    {
        var kind = Str(a, "kind") ?? "code";
        var query = Str(a, "query") ?? throw new GitHubException("query が要ります");
        var limit = Limit(a, 10);
        var (owner, repo) = OwnerRepo(a);
        var scopes = repo is not null
            ? [(owner!, $"repo:{repo}")]
            : owner is not null
                ? [(owner, Qualifier(owner))]
                : _owners.Keys.Select(o => (o, Qualifier(o))).ToList();

        var sb = new StringBuilder();
        var total = 0;
        foreach (var (o, qualifier) in scopes)
        {
            var q = Uri.EscapeDataString($"{query} {qualifier}" + (kind == "prs" ? " is:pr" : kind == "issues" ? " is:issue" : ""));
            if (kind == "code")
            {
                var r = await _client.GetAsync(o, $"/search/code?q={q}&per_page={limit}", GitHubJson.Default.SearchResultCodeItem, ct);
                total += r.TotalCount;
                foreach (var i in r.Items ?? [])
                    sb.AppendLine($"- {i.Repository?.FullName}: {i.Path} <{i.HtmlUrl}>");
            }
            else
            {
                var r = await _client.GetAsync(o, $"/search/issues?q={q}&per_page={limit}&sort=updated", GitHubJson.Default.SearchResultIssue, ct);
                total += r.TotalCount;
                foreach (var i in r.Items ?? [])
                    sb.AppendLine(Format.IssueLine(i));
            }
        }
        if (sb.Length == 0) return "該当なし";
        return $"{total} 件中、上位を表示:\n" + Format.Cap(sb.ToString(), MaxChars);
    }

    private async Task<string> ReadFile(JsonElement a, CancellationToken ct)
    {
        var (owner, repo) = RepoRequired(a);
        var path = Str(a, "path") ?? throw new GitHubException("path が要ります");
        var refQ = Str(a, "ref") is { } r ? "?ref=" + Uri.EscapeDataString(r) : "";
        var c = await _client.GetAsync(owner, $"/repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}{refQ}", GitHubJson.Default.Contents, ct);
        if (c.Type != "file") return $"{path} はファイルではありません ({c.Type})";
        if (c.Encoding != "base64" || c.Content is null) return $"{path} は中身を取得できません(サイズ {c.Size} バイト。1MB を超えるファイルは読めません)";
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(c.Content.Replace("\n", "")));
        var start = Int(a, "start_line") ?? 1;
        var end = Int(a, "end_line");
        return Format.Lines(text, start, end, 300, c.HtmlUrl);
    }

    private async Task<string> List(JsonElement a, CancellationToken ct)
    {
        var (owner, repo) = RepoRequired(a);
        var kind = Str(a, "kind") ?? "issues";
        var state = Str(a, "state") ?? "open";
        var limit = Limit(a, 20);
        var sb = new StringBuilder();
        if (kind == "prs")
        {
            var pulls = await _client.GetAsync(owner, $"/repos/{repo}/pulls?state={state}&per_page={limit}&sort=updated&direction=desc", GitHubJson.Default.ListPull, ct);
            foreach (var p in pulls) sb.AppendLine(Format.PullLine(p));
        }
        else
        {
            var labels = Str(a, "labels") is { } l ? "&labels=" + Uri.EscapeDataString(l) : "";
            // issues API は PR も混ぜて返すので落とす
            var issues = await _client.GetAsync(owner, $"/repos/{repo}/issues?state={state}&per_page={limit + 10}&sort=updated{labels}", GitHubJson.Default.ListIssue, ct);
            foreach (var i in issues.Where(i => i.PullRequest is null).Take(limit)) sb.AppendLine(Format.IssueLine(i));
        }
        return sb.Length == 0 ? "該当なし" : Format.Cap(sb.ToString(), MaxChars);
    }

    private async Task<string> Get(JsonElement a, CancellationToken ct)
    {
        var (owner, repo) = RepoRequired(a);
        var number = Int(a, "number") ?? throw new GitHubException("number が要ります");
        var issue = await _client.GetAsync(owner, $"/repos/{repo}/issues/{number}", GitHubJson.Default.Issue, ct);
        var sb = new StringBuilder();
        if (issue.PullRequest is not null)
        {
            var pr = await _client.GetAsync(owner, $"/repos/{repo}/pulls/{number}", GitHubJson.Default.Pull, ct);
            sb.AppendLine(Format.PullHeader(pr));
            var files = await _client.GetAsync(owner, $"/repos/{repo}/pulls/{number}/files?per_page=50", GitHubJson.Default.ListPullFile, ct);
            sb.AppendLine("変更ファイル:");
            foreach (var f in files) sb.AppendLine($"- {f.Filename} ({f.Status}, +{f.Additions}/-{f.Deletions})");
            if (files.Count == 50) sb.AppendLine("- …(50 件で打ち切り)");
        }
        else
        {
            sb.AppendLine(Format.IssueHeader(issue));
        }
        sb.AppendLine();
        sb.AppendLine(Format.Cap(issue.Body ?? "(本文なし)", 3000));
        if (issue.Comments > 0)
        {
            var comments = await _client.GetAsync(owner, $"/repos/{repo}/issues/{number}/comments?per_page=30", GitHubJson.Default.ListComment, ct);
            sb.AppendLine();
            sb.AppendLine($"コメント ({issue.Comments} 件):");
            foreach (var c in comments)
                sb.AppendLine($"- [{c.User?.Login}] ({Format.Date(c.CreatedAt)}) {Format.OneLine(c.Body, 400)}");
        }
        return Format.Cap(sb.ToString(), MaxChars * 2);
    }

    private async Task<string> Commits(JsonElement a, CancellationToken ct)
    {
        var (owner, repo) = RepoRequired(a);
        var q = new StringBuilder($"/repos/{repo}/commits?per_page={Limit(a, 15)}");
        if (Str(a, "ref") is { } r) q.Append("&sha=").Append(Uri.EscapeDataString(r));
        if (Str(a, "path") is { } p) q.Append("&path=").Append(Uri.EscapeDataString(p));
        if (Str(a, "since") is { } s) q.Append("&since=").Append(Uri.EscapeDataString(s));
        var commits = await _client.GetAsync(owner, q.ToString(), GitHubJson.Default.ListCommit, ct);
        if (commits.Count == 0) return "該当なし";
        var sb = new StringBuilder();
        foreach (var c in commits)
            sb.AppendLine($"- {c.Sha?[..7]} ({Format.Date(c.Detail?.Author?.Date)}, {c.Author?.Login ?? c.Detail?.Author?.Name}) {Format.OneLine(c.Detail?.Message, 120)} <{c.HtmlUrl}>");
        return Format.Cap(sb.ToString(), MaxChars);
    }

    // ---- 引数の取り出し ----

    private static string? Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static int Limit(JsonElement a, int fallback) => Math.Clamp(Int(a, "limit") ?? fallback, 1, 30);

    /// <summary>repo("owner/name")か owner のどちらかから owner を決める</summary>
    private (string? Owner, string? Repo) OwnerRepo(JsonElement a)
    {
        if (Str(a, "repo") is { } repo)
        {
            var slash = repo.IndexOf('/');
            if (slash <= 0) throw new GitHubException($"repo は owner/name の形で: {repo}");
            return (repo[..slash].ToLowerInvariant(), repo);
        }
        return (Str(a, "owner")?.ToLowerInvariant(), null);
    }

    private (string Owner, string Repo) RepoRequired(JsonElement a)
    {
        var (owner, repo) = OwnerRepo(a);
        if (repo is null) throw new GitHubException("repo(owner/name)が要ります");
        return (owner!, repo);
    }

    private string Qualifier(string owner) => (_owners.TryGetValue(owner, out var t) && t == "Organization" ? "org:" : "user:") + owner;

    /// <summary>例外はツールの失敗として LLM に返す(会話は止めない)</summary>
    private sealed class Tool(string name, string description, string schema, Func<JsonElement, CancellationToken, Task<string>> run) : ITool
    {
        public string Name => name;
        public string Description => description;
        public string InputSchemaJson => schema;

        public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            try { return new ToolResult(await run(input, ct)); }
            catch (GitHubException ex) { return new ToolResult(ex.Message, IsError: true); }
            catch (HttpRequestException ex) { return new ToolResult($"GitHub に接続できません: {ex.Message}", IsError: true); }
        }
    }
}

/// <summary>LLM 向けの整形。純粋関数なのでテストしやすい</summary>
public static class Format
{
    public static string OneLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    public static string Date(DateTimeOffset? d) => d?.ToString("yyyy-MM-dd") ?? "-";

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + $"\n…(全 {s.Length} 文字。続きは範囲を指定して)";

    public static string IssueLine(Issue i)
    {
        var labels = i.Labels is { Count: > 0 } ? " [" + string.Join(", ", i.Labels.Select(l => l.Name)) + "]" : "";
        var kind = i.PullRequest is not null ? "PR" : "Issue";
        return $"- {kind} #{i.Number} ({i.State}) {i.Title}{labels} by {i.User?.Login}, updated {Date(i.UpdatedAt)} <{i.HtmlUrl}>";
    }

    public static string PullLine(Pull p) =>
        $"- PR #{p.Number} ({(p.Merged == true ? "merged" : p.State)}{(p.Draft ? ", draft" : "")}) {p.Title} by {p.User?.Login}, {p.Head?.Ref} → {p.Base?.Ref}, updated {Date(p.UpdatedAt)} <{p.HtmlUrl}>";

    public static string IssueHeader(Issue i) =>
        $"Issue #{i.Number} ({i.State}) {i.Title}\nby {i.User?.Login}, created {Date(i.CreatedAt)}, updated {Date(i.UpdatedAt)}{(i.Labels is { Count: > 0 } ? ", labels: " + string.Join(", ", i.Labels.Select(l => l.Name)) : "")}\n<{i.HtmlUrl}>";

    public static string PullHeader(Pull p) =>
        $"PR #{p.Number} ({(p.Merged == true ? "merged" : p.State)}{(p.Draft ? ", draft" : "")}) {p.Title}\nby {p.User?.Login}, {p.Head?.Ref} → {p.Base?.Ref}, +{p.Additions}/-{p.Deletions} in {p.ChangedFiles} files, created {Date(p.CreatedAt)}, updated {Date(p.UpdatedAt)}{(p.MergedAt is { } m ? $", merged {Date(m)}" : "")}\n<{p.HtmlUrl}>";

    /// <summary>行番号付きで範囲を切り出す。end 省略時は start から maxLines 行</summary>
    public static string Lines(string text, int start, int? end, int maxLines, string? url)
    {
        var lines = text.Split('\n');
        start = Math.Max(1, start);
        var stop = Math.Min(lines.Length, end ?? start + maxLines - 1);
        if (start > lines.Length) return $"{lines.Length} 行しかありません";
        var sb = new StringBuilder();
        sb.AppendLine($"{(url is null ? "" : "<" + url + "> ")}行 {start}-{stop} / 全 {lines.Length} 行");
        sb.AppendLine("```");
        for (var i = start; i <= stop; i++) sb.Append(i).Append(": ").AppendLine(lines[i - 1].TrimEnd('\r'));
        sb.AppendLine("```");
        if (stop < lines.Length) sb.AppendLine($"…続きは start_line={stop + 1} で");
        return sb.ToString();
    }
}
