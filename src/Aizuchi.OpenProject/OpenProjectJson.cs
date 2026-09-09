using System.Text.Json.Serialization;

namespace Aizuchi.OpenProject;

// API v3 は HAL+JSON。要る項目だけ、camelCase のまま受ける
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectCollection))]
[JsonSerializable(typeof(WorkPackageCollection))]
[JsonSerializable(typeof(ActivityCollection))]
[JsonSerializable(typeof(WorkPackage))]
[JsonSerializable(typeof(Me))]
public sealed partial class OpenProjectJson : JsonSerializerContext;

/// <summary>description や comment は raw / html の両方で来る。使うのは raw だけ</summary>
public sealed class Formattable
{
    public string? Raw { get; set; }
}

/// <summary>_links の 1 本。title に表示名が入るので、状態や担当者はこれで足りる</summary>
public sealed class Link
{
    public string? Href { get; set; }
    public string? Title { get; set; }
}

public sealed class Project
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Identifier { get; set; }
    public bool Active { get; set; }
    public Formattable? Description { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class ProjectCollection
{
    public int Total { get; set; }
    public int Count { get; set; }
    [JsonPropertyName("_embedded")] public ProjectElements? Embedded { get; set; }
}

public sealed class ProjectElements
{
    public List<Project>? Elements { get; set; }
}

public sealed class WorkPackage
{
    public int Id { get; set; }
    public string? Subject { get; set; }
    public Formattable? Description { get; set; }
    public string? StartDate { get; set; }
    public string? DueDate { get; set; }
    public int? PercentageDone { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("_links")] public WorkPackageLinks? Links { get; set; }
}

public sealed class WorkPackageLinks
{
    public Link? Status { get; set; }
    public Link? Type { get; set; }
    public Link? Project { get; set; }
    public Link? Assignee { get; set; }
    public Link? Priority { get; set; }
    public Link? Author { get; set; }
}

public sealed class WorkPackageCollection
{
    public int Total { get; set; }
    public int Count { get; set; }
    [JsonPropertyName("_embedded")] public WorkPackageElements? Embedded { get; set; }
}

public sealed class WorkPackageElements
{
    public List<WorkPackage>? Elements { get; set; }
}

/// <summary>更新履歴。comment が空のものは属性変更なので落とす</summary>
public sealed class Activity
{
    public int Id { get; set; }
    public Formattable? Comment { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("_links")] public ActivityLinks? Links { get; set; }
}

public sealed class ActivityLinks
{
    public Link? User { get; set; }
}

public sealed class ActivityCollection
{
    public int Total { get; set; }
    [JsonPropertyName("_embedded")] public ActivityElements? Embedded { get; set; }
}

public sealed class ActivityElements
{
    public List<Activity>? Elements { get; set; }
}

/// <summary>起動時の疎通確認に使う /users/me</summary>
public sealed class Me
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
