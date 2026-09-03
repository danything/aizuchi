using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aizuchi.Core;

namespace Aizuchi.Claude;

public sealed record ClaudeOptions(
    string ApiKey,
    string Model,
    int MaxTokens,
    string? Effort,
    bool Fallbacks,
    string BaseUrl)
{
    public static ClaudeOptions FromEnvironment(Func<string, string?> env) => new(
        ApiKey: Env.Required(env, "ANTHROPIC_API_KEY"),
        Model: Env.Or(env, "CLAUDE_MODEL", "claude-opus-5"),
        MaxTokens: Env.PositiveInt(env, "CLAUDE_MAX_TOKENS", 16_000),
        Effort: Env.Optional(env, "CLAUDE_EFFORT"),
        Fallbacks: !string.Equals(Env.Optional(env, "CLAUDE_FALLBACKS"), "off", StringComparison.OrdinalIgnoreCase),
        BaseUrl: Env.Or(env, "ANTHROPIC_BASE_URL", "https://api.anthropic.com"));
}

public sealed class ClaudeApiException(int status, string body)
    : LlmException($"Claude API HTTP {status}", body)
{
    public int Status { get; } = status;
}

/// <summary>
/// POST /v1/messages をストリーミングで叩く ILlmProvider。
/// thinking は指定しない(= Claude Opus 5 では adaptive)。
/// </summary>
public sealed class ClaudeProvider(HttpClient http, ClaudeOptions opt) : ILlmProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    public string Name => "claude";

    public async Task<LlmResult> StreamAsync(LlmRequest request, Func<string, Task> onText, CancellationToken ct)
    {
        var body = new MessagesRequest
        {
            Model = opt.Model,
            MaxTokens = opt.MaxTokens,
            Stream = true,
            System = request.SystemPrompt,
            Messages = request.Messages.Select(m => new MessageParam { Role = m.Role, Content = m.Content }).ToList(),
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
            throw new ClaudeApiException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new SseParser();
        var text = new StringBuilder();
        string? stopReason = null, model = null;
        long input = 0, output = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (parser.Feed(line) is not { } data) continue;
            var ev = JsonSerializer.Deserialize(data, ClaudeJson.Default.StreamEvent);
            switch (ev?.Type)
            {
                case "message_start":
                    model = ev.Message?.Model;
                    input = ev.Message?.Usage?.InputTokens ?? 0;
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

        return new LlmResult(text.ToString(), ToStopKind(stopReason), model, input, output);
    }

    public static StopKind ToStopKind(string? stopReason) => stopReason switch
    {
        "max_tokens" => StopKind.Truncated,
        "refusal" => StopKind.Refused,
        _ => StopKind.Completed,
    };
}
