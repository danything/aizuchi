using System.Text;
using System.Text.Json;
using Aizuchi.Core;

namespace Aizuchi.OpenProject;

/// <summary>OpenProject を読む道具(読み取り専用)。結果は LLM が読みやすい短い Markdown にして返す</summary>
public sealed class OpenProjectToolPack : IToolPack
{
    private readonly OpenProjectClient _client;
    private readonly string _me;

    private OpenProjectToolPack(OpenProjectClient client, string me)
    {
        _client = client;
        _me = me;
        Tools =
        [
            new Tool("openproject_projects", "プロジェクトの一覧。識別子が曖昧なときはまずこれで確かめる",
                Schema("", []), Projects),
            new Tool("openproject_search", "作業パッケージを横断検索する。件名・説明・コメントから探す",
                Schema("""
                    "query": {"type": "string"},
                    "project": {"type": "string", "description": "プロジェクトの識別子か ID。指定するとその中だけ"},
                    "status": {"type": "string", "enum": ["open", "closed", "all"]},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 50}
                    """, ["query"]),
                Search),
            new Tool("openproject_list", "プロジェクト内の作業パッケージ一覧(更新の新しい順)",
                Schema("""
                    "project": {"type": "string", "description": "プロジェクトの識別子か ID"},
                    "status": {"type": "string", "enum": ["open", "closed", "all"]},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 50}
                    """, ["project"]),
                List),
            new Tool("openproject_get", "作業パッケージ 1 件の詳細とコメント",
                Schema("""
                    "id": {"type": "integer"}
                    """, ["id"]),
                Get),
        ];
    }

    public static async Task<OpenProjectToolPack> CreateAsync(OpenProjectClient client, CancellationToken ct) =>
        new(client, (await client.MeAsync(ct)).Name ?? "(不明)");

    public string Name => "openproject";
    public IReadOnlyList<ITool> Tools { get; }

    public string PromptSection => $"""
        # OpenProject
        openproject_* の道具で {_client.Url} を読めます(読み取り専用。接続ユーザーは {_me})。
        - プロジェクトは識別子(URL に出る英数字)で指定する。曖昧なら openproject_projects で確かめる
        - 横断で探すなら openproject_search、プロジェクトの中を見るなら openproject_list
        - 既定では未完了(open)だけを見る。完了分も要るときは status を指定する
        - 結果は要点だけを引用し、必ずリンクを添える
        """;

    /// <summary>結果 1 件あたりの上限。長い本文はここで切る</summary>
    private const int MaxChars = 6000;

    private static string Schema(string properties, string[] required) =>
        "{\"type\": \"object\", \"properties\": {" + properties + "}, \"required\": [" +
        string.Join(", ", required.Select(r => "\"" + r + "\"")) + "], \"additionalProperties\": false}";

    // ---- 各道具 ----

    private async Task<string> Projects(JsonElement a, CancellationToken ct)
    {
        var res = await _client.GetAsync("/projects?pageSize=200&sortBy=" + Uri.EscapeDataString("""[["name","asc"]]"""),
            OpenProjectJson.Default.ProjectCollection, ct);
        var sb = new StringBuilder();
        foreach (var p in (res.Embedded?.Elements ?? []).Where(p => p.Active))
            sb.AppendLine($"- {p.Identifier} (#{p.Id}) {p.Name}: {Format.OneLine(p.Description?.Raw, 120)} <{_client.Url}/projects/{p.Identifier}>");
        return sb.Length == 0 ? "プロジェクトが見つかりません" : Format.Cap(sb.ToString(), MaxChars);
    }

    private async Task<string> Search(JsonElement a, CancellationToken ct)
    {
        var query = Str(a, "query") ?? throw new OpenProjectException("query が要ります");
        var scope = Str(a, "project") is { } p ? $"/projects/{Uri.EscapeDataString(p)}" : "";
        var filters = new List<string> { StatusFilter(Str(a, "status") ?? "open") };

        // 全文検索のフィルタ名はバージョンで違う。search で試し、通らなければ件名 / ID に落とす
        WorkPackageCollection res;
        try { res = await Query(scope, [.. filters, Filter("search", "**", query)], Limit(a, 20), ct); }
        catch (OpenProjectException)
        { res = await Query(scope, [.. filters, Filter("subjectOrId", "**", query)], Limit(a, 20), ct); }

        return Render(res, "該当なし");
    }

    private async Task<string> List(JsonElement a, CancellationToken ct)
    {
        var project = Str(a, "project") ?? throw new OpenProjectException("project(識別子か ID)が要ります");
        var res = await Query($"/projects/{Uri.EscapeDataString(project)}",
            [StatusFilter(Str(a, "status") ?? "open")], Limit(a, 20), ct);
        return Render(res, "該当なし");
    }

    private async Task<string> Get(JsonElement a, CancellationToken ct)
    {
        var id = Int(a, "id") ?? throw new OpenProjectException("id が要ります");
        var wp = await _client.GetAsync($"/work_packages/{id}", OpenProjectJson.Default.WorkPackage, ct);
        var sb = new StringBuilder();
        sb.AppendLine(Format.Header(wp, _client.Url));
        sb.AppendLine();
        sb.AppendLine(Format.Cap(wp.Description?.Raw is { Length: > 0 } d ? d : "(本文なし)", 3000));

        var acts = await _client.GetAsync($"/work_packages/{id}/activities?pageSize=50",
            OpenProjectJson.Default.ActivityCollection, ct);
        // 属性変更だけの履歴は comment が空。人が書いたものだけ拾う
        var comments = (acts.Embedded?.Elements ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Comment?.Raw)).ToList();
        if (comments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"コメント ({comments.Count} 件):");
            foreach (var c in comments)
                sb.AppendLine($"- [{c.Links?.User?.Title}] ({Format.Date(c.CreatedAt)}) {Format.OneLine(c.Comment?.Raw, 400)}");
        }
        return Format.Cap(sb.ToString(), MaxChars * 2);
    }

    // ---- 問い合わせの組み立て ----

    private Task<WorkPackageCollection> Query(string scope, string[] filters, int limit, CancellationToken ct)
    {
        var q = $"?filters={Uri.EscapeDataString("[" + string.Join(",", filters) + "]")}" +
                $"&pageSize={limit}&sortBy={Uri.EscapeDataString("""[["updatedAt","desc"]]""")}";
        return _client.GetAsync(scope + "/work_packages" + q, OpenProjectJson.Default.WorkPackageCollection, ct);
    }

    /// <summary>status は値ではなく演算子で表す(o = 未完了、c = 完了)</summary>
    private static string StatusFilter(string status) => status switch
    {
        "closed" => Filter("status", "c"),
        "all" => Filter("status", "*"),
        _ => Filter("status", "o"),
    };

    private static string Filter(string name, string op, params string[] values) =>
        "{\"" + name + "\":{\"operator\":\"" + op + "\",\"values\":[" +
        string.Join(",", values.Select(v => "\"" + JsonEncodedText.Encode(v) + "\"")) + "]}}";

    private string Render(WorkPackageCollection res, string empty)
    {
        var items = res.Embedded?.Elements ?? [];
        if (items.Count == 0) return empty;
        var sb = new StringBuilder();
        foreach (var w in items) sb.AppendLine(Format.Line(w, _client.Url));
        return $"{res.Total} 件中、上位を表示:\n" + Format.Cap(sb.ToString(), MaxChars);
    }

    // ---- 引数の取り出し ----

    private static string? Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static int Limit(JsonElement a, int fallback) => Math.Clamp(Int(a, "limit") ?? fallback, 1, 50);

    /// <summary>例外はツールの失敗として LLM に返す(会話は止めない)</summary>
    private sealed class Tool(string name, string description, string schema, Func<JsonElement, CancellationToken, Task<string>> run) : ITool
    {
        public string Name => name;
        public string Description => description;
        public string InputSchemaJson => schema;

        public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            try { return new ToolResult(await run(input, ct)); }
            catch (OpenProjectException ex) { return new ToolResult(ex.Message, IsError: true); }
            catch (HttpRequestException ex) { return new ToolResult($"OpenProject に接続できません: {ex.Message}", IsError: true); }
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

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + $"\n…(全 {s.Length} 文字。続きは範囲を絞って)";

    public static string Line(WorkPackage w, string baseUrl)
    {
        var l = w.Links;
        var kind = l?.Type?.Title is { } t ? $"[{t}]" : "";
        var who = l?.Assignee?.Title is { } who2 ? $", 担当: {who2}" : "";
        return $"- #{w.Id} ({l?.Status?.Title}) {w.Subject} {kind}{who}, 更新 {Date(w.UpdatedAt)} <{baseUrl}/work_packages/{w.Id}>";
    }

    public static string Header(WorkPackage w, string baseUrl)
    {
        var l = w.Links;
        var dates = (w.StartDate ?? w.DueDate) is null ? "" : $"\n開始 {w.StartDate ?? "-"} / 期日 {w.DueDate ?? "-"}, 進捗 {w.PercentageDone ?? 0}%";
        return $"#{w.Id} ({l?.Status?.Title}) {w.Subject}\n" +
               $"{l?.Type?.Title} / {l?.Priority?.Title}, プロジェクト: {l?.Project?.Title}, 作成者: {l?.Author?.Title}, 担当: {l?.Assignee?.Title ?? "なし"}{dates}\n" +
               $"作成 {Date(w.CreatedAt)}, 更新 {Date(w.UpdatedAt)}\n<{baseUrl}/work_packages/{w.Id}>";
    }
}
