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
                    "sprint": {"type": "integer", "description": "スプリント ID。openproject_sprints で調べる"},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 50},
                    "page": {"type": "integer", "minimum": 1, "description": "1 始まりのページ番号"}
                    """, ["query"]),
                Search),
            new Tool("openproject_list", "プロジェクト内の作業パッケージ一覧(更新の新しい順)",
                Schema("""
                    "project": {"type": "string", "description": "プロジェクトの識別子か ID"},
                    "status": {"type": "string", "enum": ["open", "closed", "all"]},
                    "sprint": {"type": "integer", "description": "スプリント ID。openproject_sprints で調べる"},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 50},
                    "page": {"type": "integer", "minimum": 1, "description": "1 始まりのページ番号"}
                    """, ["project"]),
                List),
            new Tool("openproject_get", "作業パッケージ 1 件の詳細とコメント",
                Schema("""
                    "id": {"type": "integer"}
                    """, ["id"]),
                Get),
            new Tool("openproject_sprints", "プロジェクトのスプリント一覧(Backlogs)。進行中のものが分かる",
                Schema("""
                    "project": {"type": "string", "description": "プロジェクトの識別子か ID"}
                    """, ["project"]),
                SprintList),
            new Tool("openproject_velocity", "スプリント別のベロシティ。クローズ扱いの作業パッケージの storyPoints を合計する",
                Schema("""
                    "project": {"type": "string", "description": "プロジェクトの識別子か ID"},
                    "sprints": {"type": "integer", "minimum": 1, "maximum": 20, "description": "新しい方から何スプリント見るか(既定 6)"}
                    """, ["project"]),
                Velocity),
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
        - ストーリーポイントは storyPoints。本文に書かれた「見積 0.5日 (SP1)」のような表記は
          移行前の手運用なので、storyPoints が出ているならそちらは読まない
        - ベロシティはスプリント単位が正。週あたりに直した値は換算でしかない
        - 完了日は API から素直に取れない。日付で切った集計は updatedAt 代用になり誤差が出るので、
          そう断ってから答える
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
        var filters = Filters(a);

        // 全文検索のフィルタ名はバージョンで違う。search で試し、通らなければ件名 / ID に落とす
        WorkPackageCollection res;
        try { res = await QueryPage(scope, [.. filters, Filter("search", "**", query)], Limit(a, 20), Page(a), ct); }
        catch (OpenProjectException)
        { res = await QueryPage(scope, [.. filters, Filter("subjectOrId", "**", query)], Limit(a, 20), Page(a), ct); }

        return Render(res, "該当なし");
    }

    private async Task<string> List(JsonElement a, CancellationToken ct)
    {
        var project = Str(a, "project") ?? throw new OpenProjectException("project(識別子か ID)が要ります");
        var res = await QueryPage($"/projects/{Uri.EscapeDataString(project)}",
            Filters(a), Limit(a, 20), Page(a), ct);
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

    private async Task<string> SprintList(JsonElement a, CancellationToken ct)
    {
        var project = Str(a, "project") ?? throw new OpenProjectException("project(識別子か ID)が要ります");
        var sprints = await FetchSprints(project, ct);
        if (sprints.Count == 0)
            return "スプリントがありません(プロジェクトで Backlogs モジュールが有効か確認してください)";
        var sb = new StringBuilder();
        foreach (var s in Recent(sprints, sprints.Count))
            sb.AppendLine($"- #{s.Id} {s.Name} ({Format.Period(s)}) {Format.SprintStatus(s)}{(Format.IsActive(s) ? " ← 進行中" : "")}");
        return Format.Cap(sb.ToString(), MaxChars);
    }

    /// <summary>
    /// スプリント別のベロシティ。クローズ扱いの WP の storyPoints を合計する
    /// (OpenProject のバーンダウンと同じ数え方)。
    /// </summary>
    private async Task<string> Velocity(JsonElement a, CancellationToken ct)
    {
        var project = Str(a, "project") ?? throw new OpenProjectException("project(識別子か ID)が要ります");
        var sprints = await FetchSprints(project, ct);
        if (sprints.Count == 0)
            return "スプリントがありません(プロジェクトで Backlogs モジュールが有効か確認してください)";

        var scope = $"/projects/{Uri.EscapeDataString(project)}";
        var sb = new StringBuilder("スプリント別ベロシティ(クローズ扱いの storyPoints 合計):\n");
        var unset = 0;
        foreach (var s in Recent(sprints, Math.Clamp(Int(a, "sprints") ?? 6, 1, 20)))
        {
            var done = await QueryAll(scope, [SprintFilter(s.Id), Filter("status", "c")], ct);
            var points = done.Sum(w => w.StoryPoints ?? 0);
            var missing = done.Count(w => w.StoryPoints is null);
            unset += missing;
            sb.Append($"- #{s.Id} {s.Name} ({Format.Period(s)}){(Format.IsActive(s) ? " [進行中]" : "")}: ")
              .Append($"{points} SP / 完了 {done.Count} 件")
              .AppendLine(missing > 0 ? $"(うち {missing} 件は storyPoints 未設定)" : "");
        }
        if (unset > 0)
            sb.AppendLine("\n※ storyPoints が未設定の作業パッケージは 0 として数えています。" +
                "そのタイプが管理画面の Story types に入っていないと storyPoints は返りません(タスクは remainingTime 側)");
        sb.AppendLine("※ 週あたりに直す場合はスプリント期間で割った換算値です。完了日は API から取れないため、日付で切ると updatedAt 代用の誤差が出ます");
        return Format.Cap(sb.ToString(), MaxChars);
    }

    private async Task<List<Sprint>> FetchSprints(string project, CancellationToken ct) =>
        (await _client.GetAsync($"/projects/{Uri.EscapeDataString(project)}/sprints",
            OpenProjectJson.Default.SprintCollection, ct)).Embedded?.Elements ?? [];

    /// <summary>開始日の新しい順。日付が無いものは後ろに送る</summary>
    private static IEnumerable<Sprint> Recent(List<Sprint> sprints, int take) =>
        sprints.OrderByDescending(s => s.StartDate ?? "").ThenByDescending(s => s.Id).Take(take);

    // ---- 問い合わせの組み立て ----

    private Task<WorkPackageCollection> QueryPage(string scope, string[] filters, int pageSize, int page, CancellationToken ct)
    {
        // OpenProject の offset はページ番号(1 始まり)。件数のオフセットではない
        var q = $"?filters={Uri.EscapeDataString("[" + string.Join(",", filters) + "]")}" +
                $"&pageSize={pageSize}&offset={page}&sortBy={Uri.EscapeDataString("""[["updatedAt","desc"]]""")}";
        return _client.GetAsync(scope + "/work_packages" + q, OpenProjectJson.Default.WorkPackageCollection, ct);
    }

    /// <summary>集計用に全ページ取る。返りが pageSize 未満になったら終わり</summary>
    private async Task<List<WorkPackage>> QueryAll(string scope, string[] filters, CancellationToken ct)
    {
        const int pageSize = 100, maxPages = 50;
        var all = new List<WorkPackage>();
        for (var page = 1; page <= maxPages; page++)
        {
            var items = (await QueryPage(scope, filters, pageSize, page, ct)).Embedded?.Elements ?? [];
            all.AddRange(items);
            if (items.Count < pageSize) break;
        }
        return all;
    }

    /// <summary>一覧・検索で共通の絞り込み(状態とスプリント)</summary>
    private static string[] Filters(JsonElement a)
    {
        var filters = new List<string> { StatusFilter(Str(a, "status") ?? "open") };
        if (Int(a, "sprint") is { } sprint) filters.Add(SprintFilter(sprint));
        return [.. filters];
    }

    private static string SprintFilter(int id) =>
        Filter("sprint", "=", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    /// <summary>1 始まりのページ番号</summary>
    private static int Page(JsonElement a) => Math.Max(1, Int(a, "page") ?? 1);

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
        var sp = w.StoryPoints is { } p ? $", SP{p}" : "";
        var iter = Iteration(w) is { } it ? $", {it}" : "";
        var who = l?.Assignee?.Title is { } a ? $", 担当: {a}" : "";
        var done = w.PercentageDone is > 0 and { } d ? $", 進捗 {d}%" : "";
        return $"- #{w.Id} ({l?.Status?.Title}) {w.Subject} {kind}{sp}{iter}{who}{done}, 更新 {Date(w.UpdatedAt)} <{baseUrl}/work_packages/{w.Id}>";
    }

    /// <summary>スプリント。Backlogs が無いプロジェクトではバージョンで代用する</summary>
    public static string? Iteration(WorkPackage w) =>
        w.Links?.Sprint?.Title ?? w.Links?.Version?.Title;

    public static string Period(Sprint s) =>
        $"{s.StartDate ?? "-"} 〜 {s.EndDate ?? s.EffectiveDate ?? "-"}";

    public static bool IsActive(Sprint s) =>
        s.Links?.Status?.Href?.EndsWith(":active", StringComparison.Ordinal) == true;

    public static string SprintStatus(Sprint s) =>
        s.Links?.Status?.Title ?? s.Links?.Status?.Href?.Split(':').LastOrDefault() ?? "-";

    public static string Header(WorkPackage w, string baseUrl)
    {
        var l = w.Links;
        var dates = (w.StartDate ?? w.DueDate) is null ? "" : $"\n開始 {w.StartDate ?? "-"} / 期日 {w.DueDate ?? "-"}, 進捗 {w.PercentageDone ?? 0}%";
        var sp = w.StoryPoints is { } p ? $"{p}" : "未設定";
        return $"#{w.Id} ({l?.Status?.Title}) {w.Subject}\n" +
               $"{l?.Type?.Title} / {l?.Priority?.Title}, プロジェクト: {l?.Project?.Title}, 作成者: {l?.Author?.Title}, 担当: {l?.Assignee?.Title ?? "なし"}\n" +
               $"ストーリーポイント: {sp}, スプリント: {Iteration(w) ?? "なし"}{dates}\n" +
               $"作成 {Date(w.CreatedAt)}, 更新 {Date(w.UpdatedAt)}\n<{baseUrl}/work_packages/{w.Id}>";
    }
}
