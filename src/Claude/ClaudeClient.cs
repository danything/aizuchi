using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SlackClaudeBot.Claude;

public sealed record ChatMessage(string Role, string Content);

public sealed record ClaudeOptions(
    string ApiKey,
    string Model,
    int MaxTokens,
    string? Effort,
    string SystemPrompt,
    bool Fallbacks,
    string BaseUrl);

public sealed record ClaudeResult(
    string Text,
    string? StopReason,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens);

public sealed class ClaudeApiException(int status, string body)
    : Exception($"Claude API が HTTP {status}: {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}

/// <summary>
/// POST /v1/messages をストリーミングで叩く。公式 SDK は Native AOT で動かないので HttpClient 直叩き。
/// thinking は指定しない(= Claude Opus 5 では adaptive)。
/// </summary>
public sealed class ClaudeClient(HttpClient http, ClaudeOptions opt)
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public async Task<ClaudeResult> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        Func<string, Task> onText,
        CancellationToken ct)
    {
        var body = new MessagesRequest
        {
            Model = opt.Model,
            MaxTokens = opt.MaxTokens,
            Stream = true,
            System = opt.SystemPrompt,
            Messages = messages.Select(m => new MessageParam { Role = m.Role, Content = m.Content }).ToList(),
            OutputConfig = opt.Effort is null ? null : new OutputConfig { Effort = opt.Effort },
            Fallbacks = opt.Fallbacks ? "default" : null,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opt.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(body, ClaudeJson.Default.MessagesRequest),
        };
        req.Headers.Add("x-api-key", opt.ApiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        if (opt.Fallbacks) req.Headers.Add("anthropic-beta", FallbackBeta);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new ClaudeApiException((int)res.StatusCode, err);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new SseParser();
        var text = new StringBuilder();
        string? stopReason = null, model = null;
        long input = 0, output = 0, cacheRead = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (parser.Feed(line) is not { } data) continue;
            var ev = JsonSerializer.Deserialize(data, ClaudeJson.Default.StreamEvent);
            switch (ev?.Type)
            {
                case "message_start":
                    model = ev.Message?.Model;
                    input = ev.Message?.Usage?.InputTokens ?? 0;
                    cacheRead = ev.Message?.Usage?.CacheReadInputTokens ?? 0;
                    break;
                case "content_block_delta" when ev.Delta is { Type: "text_delta", Text: { } t }:
                    text.Append(t);
                    await onText(t);
                    break;
                case "message_delta":
                    stopReason = ev.Delta?.StopReason ?? stopReason;
                    output = ev.Usage?.OutputTokens ?? output;
                    break;
                case "error":
                    throw new ClaudeApiException(0, $"{ev.Error?.Type}: {ev.Error?.Message}");
            }
        }

        return new ClaudeResult(text.ToString(), stopReason, model, input, output, cacheRead);
    }
}
