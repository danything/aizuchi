using System.Text.Json.Serialization;

namespace Aizuchi.Claude;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MessagesRequest))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(ErrorResponse))]
public sealed partial class ClaudeJson : JsonSerializerContext;

/// <summary>POST /v1/messages の本文(必要な項目だけ)</summary>
public sealed class MessagesRequest
{
    public required string Model { get; set; }
    public required int MaxTokens { get; set; }
    public bool Stream { get; set; } = true;
    public string? System { get; set; }
    public required List<MessageParam> Messages { get; set; }
    public OutputConfig? OutputConfig { get; set; }
    /// <summary>"default" で拒絶カテゴリに応じたサーバー側フォールバック(beta)</summary>
    public string? Fallbacks { get; set; }
}

public sealed class MessageParam
{
    public required string Role { get; set; }
    public required string Content { get; set; }
}

public sealed class OutputConfig
{
    public string? Effort { get; set; }
}

/// <summary>SSE の data 部分。イベント種別ごとに使うフィールドが違うので全部 nullable で一つにまとめる</summary>
public sealed class StreamEvent
{
    public string? Type { get; set; }
    public int? Index { get; set; }
    public StreamDelta? Delta { get; set; }
    public StreamMessage? Message { get; set; }
    public Usage? Usage { get; set; }
    public ApiError? Error { get; set; }
}

public sealed class StreamDelta
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? StopReason { get; set; }
}

public sealed class StreamMessage
{
    public string? Id { get; set; }
    public string? Model { get; set; }
    public Usage? Usage { get; set; }
}

public sealed class Usage
{
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadInputTokens { get; set; }
    public long? CacheCreationInputTokens { get; set; }
}

public sealed class ApiError
{
    public string? Type { get; set; }
    public string? Message { get; set; }
}

public sealed class ErrorResponse
{
    public string? Type { get; set; }
    public ApiError? Error { get; set; }
}
