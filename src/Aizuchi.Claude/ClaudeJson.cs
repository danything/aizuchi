using System.Text.Json;
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
    public List<ToolParam>? Tools { get; set; }
    public OutputConfig? OutputConfig { get; set; }
    /// <summary>"default" で拒絶カテゴリに応じたサーバー側フォールバック(beta)</summary>
    public string? Fallbacks { get; set; }
}

/// <summary>content は常にブロックの配列で送る(文字列は糖衣なので使わない)</summary>
public sealed class MessageParam
{
    public required string Role { get; set; }
    public required List<ContentBlockParam> Content { get; set; }

    public static MessageParam Text(string role, string text) =>
        new() { Role = role, Content = [new ContentBlockParam { Type = "text", Text = text }] };
}

/// <summary>
/// text / thinking / redacted_thinking / tool_use / tool_result を 1 つの型で表す。
/// 応答のブロックをそのまま次の要求に返せるように、thinking の署名も持つ。
/// </summary>
public sealed class ContentBlockParam
{
    public required string Type { get; set; }
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public string? Signature { get; set; }
    /// <summary>redacted_thinking の中身</summary>
    public string? Data { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }
    public string? ToolUseId { get; set; }
    public string? Content { get; set; }
    public bool? IsError { get; set; }
}

public sealed class ToolParam
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required JsonElement InputSchema { get; set; }
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
    public StreamContentBlock? ContentBlock { get; set; }
    public StreamDelta? Delta { get; set; }
    public StreamMessage? Message { get; set; }
    public Usage? Usage { get; set; }
    public ApiError? Error { get; set; }
}

/// <summary>content_block_start で来るブロックの頭</summary>
public sealed class StreamContentBlock
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public string? Data { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class StreamDelta
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? PartialJson { get; set; }
    public string? Thinking { get; set; }
    public string? Signature { get; set; }
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
