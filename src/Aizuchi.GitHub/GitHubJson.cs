using System.Text.Json.Serialization;

namespace Aizuchi.GitHub;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<Installation>))]
[JsonSerializable(typeof(InstallationToken))]
[JsonSerializable(typeof(InstallationRepos))]
[JsonSerializable(typeof(List<Repo>))]
[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(SearchResult<CodeItem>))]
[JsonSerializable(typeof(SearchResult<Issue>))]
[JsonSerializable(typeof(List<Issue>))]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(List<Comment>))]
[JsonSerializable(typeof(Pull))]
[JsonSerializable(typeof(List<Pull>))]
[JsonSerializable(typeof(List<PullFile>))]
[JsonSerializable(typeof(Contents))]
[JsonSerializable(typeof(List<Commit>))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(JwtHeader))]
[JsonSerializable(typeof(JwtPayload))]
public sealed partial class GitHubJson : JsonSerializerContext;

public sealed class JwtHeader
{
    public string Alg { get; set; } = "RS256";
    public string Typ { get; set; } = "JWT";
}

public sealed class JwtPayload
{
    public long Iat { get; set; }
    public long Exp { get; set; }
    public required string Iss { get; set; }
}

public sealed class Installation
{
    public long Id { get; set; }
    public Account? Account { get; set; }
}

public sealed class Account
{
    public string? Login { get; set; }
    /// <summary>"Organization" か "User"</summary>
    public string? Type { get; set; }
}

public sealed class InstallationToken
{
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class InstallationRepos
{
    public int TotalCount { get; set; }
    public List<Repo>? Repositories { get; set; }
}

public sealed class Repo
{
    public string? FullName { get; set; }
    public string? Description { get; set; }
    public bool Private { get; set; }
    public bool Archived { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class SearchResult<T>
{
    public int TotalCount { get; set; }
    public List<T>? Items { get; set; }
}

public sealed class CodeItem
{
    public string? Path { get; set; }
    public Repo? Repository { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class Label
{
    public string? Name { get; set; }
}

public sealed class Issue
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? Body { get; set; }
    public Account? User { get; set; }
    public List<Label>? Labels { get; set; }
    public int Comments { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? HtmlUrl { get; set; }
    /// <summary>issues API では PR もこの形で返る。これが付いていれば PR</summary>
    public PullRef? PullRequest { get; set; }
    public string? RepositoryUrl { get; set; }
}

public sealed class PullRef
{
    public string? Url { get; set; }
}

public sealed class Comment
{
    public Account? User { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class Pull
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? Body { get; set; }
    public Account? User { get; set; }
    public List<Label>? Labels { get; set; }
    public bool Draft { get; set; }
    public bool? Merged { get; set; }
    public bool? Mergeable { get; set; }
    public int? Additions { get; set; }
    public int? Deletions { get; set; }
    public int? ChangedFiles { get; set; }
    public GitRef? Head { get; set; }
    public GitRef? Base { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class GitRef
{
    public string? Ref { get; set; }
    public string? Sha { get; set; }
}

public sealed class PullFile
{
    public string? Filename { get; set; }
    public string? Status { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

public sealed class Contents
{
    public string? Type { get; set; }
    public long Size { get; set; }
    public string? Encoding { get; set; }
    public string? Content { get; set; }
    public string? Path { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class Commit
{
    public string? Sha { get; set; }
    [JsonPropertyName("commit")]
    public CommitDetail? Detail { get; set; }
    public Account? Author { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class CommitDetail
{
    public string? Message { get; set; }
    public CommitAuthor? Author { get; set; }
}

public sealed class CommitAuthor
{
    public string? Name { get; set; }
    public DateTimeOffset? Date { get; set; }
}

public sealed class ErrorBody
{
    public string? Message { get; set; }
}
