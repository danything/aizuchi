using System.Text.Json.Serialization;

namespace Aizuchi.Slack;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Ack))]
[JsonSerializable(typeof(ConnectionsOpenResponse))]
[JsonSerializable(typeof(AuthTestResponse))]
[JsonSerializable(typeof(PostMessageRequest))]
[JsonSerializable(typeof(UpdateMessageRequest))]
[JsonSerializable(typeof(PostMessageResponse))]
[JsonSerializable(typeof(MessagesResponse))]
[JsonSerializable(typeof(UserInfoResponse))]
public sealed partial class SlackJson : JsonSerializerContext;

/// <summary>Socket Mode の封筒。type は hello / events_api / disconnect など</summary>
public sealed class Envelope
{
    public string? Type { get; set; }
    public string? EnvelopeId { get; set; }
    public string? Reason { get; set; }
    public EventPayload? Payload { get; set; }
}

public sealed class EventPayload
{
    public string? EventId { get; set; }
    public SlackEvent? Event { get; set; }
}

/// <summary>message / app_mention イベントの共通部分</summary>
public sealed class SlackEvent
{
    public string? Type { get; set; }
    public string? Subtype { get; set; }
    public string? Channel { get; set; }
    public string? ChannelType { get; set; }
    public string? User { get; set; }
    public string? BotId { get; set; }
    public string? Text { get; set; }
    public string? Ts { get; set; }
    public string? ThreadTs { get; set; }
}

public sealed class Ack
{
    public required string EnvelopeId { get; set; }
}

public sealed class ConnectionsOpenResponse
{
    public bool Ok { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
}

public sealed class AuthTestResponse
{
    public bool Ok { get; set; }
    public string? UserId { get; set; }
    public string? BotId { get; set; }
    public string? Error { get; set; }
}

public sealed class PostMessageRequest
{
    public required string Channel { get; set; }
    public required string Text { get; set; }
    public string? ThreadTs { get; set; }
}

public sealed class UpdateMessageRequest
{
    public required string Channel { get; set; }
    public required string Ts { get; set; }
    public required string Text { get; set; }
}

public sealed class PostMessageResponse
{
    public bool Ok { get; set; }
    public string? Ts { get; set; }
    public string? Channel { get; set; }
    public string? Error { get; set; }
}

public sealed class MessagesResponse
{
    public bool Ok { get; set; }
    public List<SlackMessage>? Messages { get; set; }
    public string? Error { get; set; }
}

public sealed class SlackMessage
{
    public string? Type { get; set; }
    public string? Subtype { get; set; }
    public string? User { get; set; }
    public string? BotId { get; set; }
    public string? Text { get; set; }
    public string? Ts { get; set; }
    public string? ThreadTs { get; set; }
}

public sealed class UserInfoResponse
{
    public bool Ok { get; set; }
    public SlackUser? User { get; set; }
    public string? Error { get; set; }
}

public sealed class SlackUser
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? RealName { get; set; }
    public SlackProfile? Profile { get; set; }
}

public sealed class SlackProfile
{
    public string? DisplayName { get; set; }
    public string? RealName { get; set; }
}
