using System.Text.Json.Serialization;

namespace Aizuchi.Anarlog;

// Anarlog の webhook 本文。snake_case。要る項目だけ受ける(知らない項目は無視される)
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Envelope))]
public sealed partial class AnarlogJson : JsonSerializerContext;

/// <summary>{ id: "evt_…", event, created_at, data }</summary>
public sealed class Envelope
{
    public string? Id { get; set; }
    public string? Event { get; set; }
    public string? CreatedAt { get; set; }
    public Payload? Data { get; set; }
}

/// <summary>会議イベントは meeting + transcript_text、webhook.test は message だけ</summary>
public sealed class Payload
{
    public Meeting? Meeting { get; set; }
    public string? TranscriptText { get; set; }
    public string? Message { get; set; }
}

public sealed class Meeting
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? StartedAt { get; set; }
    public string? CreatedAt { get; set; }
    public Document? Note { get; set; }
    /// <summary>AI 要約。有料の Slack 連携が投稿するのは先頭 1 件</summary>
    public List<Document>? Summaries { get; set; }
    public List<Participant>? Participants { get; set; }
    public List<ActionItem>? ActionItems { get; set; }
}

public sealed class Document
{
    public string? Title { get; set; }
    public string? Markdown { get; set; }
}

public sealed class Participant
{
    public string? DisplayName { get; set; }
}

public sealed class ActionItem
{
    public string? Text { get; set; }
    public string? Status { get; set; }
    public string? DueAt { get; set; }
    public string? CompletedAt { get; set; }
}
