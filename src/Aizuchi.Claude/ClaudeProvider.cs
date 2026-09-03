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
/// ツールが渡されたら stop_reason=tool_use のたびに実行して結果を返し、続きを生成する。
/// </summary>
public sealed class ClaudeProvider(HttpClient http, ClaudeOptions opt) : ILlmProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    /// <summary>ツール往復の上限。記憶の追記なら 1〜2 回、GitHub を調べる依頼で 3〜6 回ほど</summary>
    private const int MaxToolRounds = 10;

    public string Name => "claude";

    public async Task<LlmResult> StreamAsync(LlmRequest request, Func<string, Task> onText, CancellationToken ct)
    {
        var messages = request.Messages.Select(m => MessageParam.Text(m.Role, m.Content)).ToList();
        var tools = request.Tools.Count == 0 ? null : request.Tools.Select(t => new ToolParam
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonDocument.Parse(t.InputSchemaJson).RootElement.Clone(),
        }).ToList();
        var toolsByName = request.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var text = new StringBuilder();
        string? model = null;
        long input = 0, output = 0;
        var toolCalls = 0;
        string? stopReason = null;

        for (var round = 0; ; round++)
        {
            var turn = await StreamOnce(messages, tools, request.SystemPrompt, async t =>
            {
                text.Append(t);
                await onText(t);
            }, ct);
            model ??= turn.Model;
            input += turn.InputTokens;
            output += turn.OutputTokens;
            stopReason = turn.StopReason;

            if (turn.StopReason != "tool_use" || tools is null) break;
            if (round >= MaxToolRounds)
                throw new LlmException("ツール呼び出しが多すぎます", $"{MaxToolRounds} 回を超えた");

            // 応答ブロック(thinking を含む)をそのまま返し、tool_result を user で続ける
            messages.Add(new MessageParam { Role = "assistant", Content = turn.Blocks });
            var results = new List<ContentBlockParam>();
            foreach (var block in turn.Blocks.Where(b => b.Type == "tool_use"))
            {
                toolCalls++;
                ToolResult result;
                if (!toolsByName.TryGetValue(block.Name!, out var tool))
                    result = new ToolResult($"未知のツール: {block.Name}", IsError: true);
                else
                {
                    try { result = await tool.InvokeAsync(block.Input ?? JsonDocument.Parse("{}").RootElement, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { result = new ToolResult($"ツールが失敗: {ex.Message}", IsError: true); }
                }
                results.Add(new ContentBlockParam
                {
                    Type = "tool_result",
                    ToolUseId = block.Id,
                    Content = result.Content,
                    IsError = result.IsError ? true : null,
                });
            }
            messages.Add(new MessageParam { Role = "user", Content = results });
        }

        return new LlmResult(text.ToString(), ToStopKind(stopReason), model, input, output, toolCalls);
    }

    private sealed record Turn(List<ContentBlockParam> Blocks, string? StopReason, string? Model, long InputTokens, long OutputTokens);

    /// <summary>1 回の要求。ブロックを組み立てながら text だけを外に流す</summary>
    private async Task<Turn> StreamOnce(List<MessageParam> messages, List<ToolParam>? tools, string system,
        Func<string, Task> onText, CancellationToken ct)
    {
        var body = new MessagesRequest
        {
            Model = opt.Model,
            MaxTokens = opt.MaxTokens,
            Stream = true,
            System = system,
            Messages = messages,
            Tools = tools,
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
        var builder = new BlockBuilder();
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
                case "content_block_start" when ev.ContentBlock is { } start:
                    builder.Start(ev.Index ?? builder.Count, start);
                    break;
                case "content_block_delta" when ev.Delta is { } delta:
                    builder.Delta(ev.Index ?? builder.Count - 1, delta);
                    if (delta is { Type: "text_delta", Text: { } t }) await onText(t);
                    break;
                case "message_delta":
                    stopReason = ev.Delta?.StopReason ?? stopReason;
                    output = ev.Usage?.OutputTokens ?? output;
                    break;
                case "error":
                    throw new ClaudeApiException(0, $"{ev.Error?.Type}: {ev.Error?.Message}");
            }
        }

        return new Turn(builder.Finish(), stopReason, model, input, output);
    }

    public static StopKind ToStopKind(string? stopReason) => stopReason switch
    {
        "max_tokens" => StopKind.Truncated,
        "refusal" => StopKind.Refused,
        _ => StopKind.Completed,
    };
}

/// <summary>ストリームの断片から応答ブロックを組み立てる。次の要求にそのまま返せる形にする</summary>
public sealed class BlockBuilder
{
    private sealed class Pending(string type)
    {
        public string Type = type;
        public string? Id, Name, Data;
        public StringBuilder Text = new(), Thinking = new(), Signature = new(), Json = new();
    }

    private readonly SortedDictionary<int, Pending> _blocks = [];

    public int Count => _blocks.Count;

    public void Start(int index, StreamContentBlock start)
    {
        var p = new Pending(start.Type ?? "text") { Id = start.Id, Name = start.Name, Data = start.Data };
        if (start.Text is { Length: > 0 } t) p.Text.Append(t);
        if (start.Thinking is { Length: > 0 } th) p.Thinking.Append(th);
        _blocks[index] = p;
    }

    public void Delta(int index, StreamDelta delta)
    {
        if (!_blocks.TryGetValue(index, out var p)) return;
        switch (delta.Type)
        {
            case "text_delta": p.Text.Append(delta.Text); break;
            case "input_json_delta": p.Json.Append(delta.PartialJson); break;
            case "thinking_delta": p.Thinking.Append(delta.Thinking); break;
            case "signature_delta": p.Signature.Append(delta.Signature); break;
        }
    }

    public List<ContentBlockParam> Finish()
    {
        var list = new List<ContentBlockParam>();
        foreach (var p in _blocks.Values)
        {
            switch (p.Type)
            {
                case "text":
                    if (p.Text.Length > 0) list.Add(new ContentBlockParam { Type = "text", Text = p.Text.ToString() });
                    break;
                case "thinking":
                    list.Add(new ContentBlockParam { Type = "thinking", Thinking = p.Thinking.ToString(), Signature = p.Signature.ToString() });
                    break;
                case "redacted_thinking":
                    list.Add(new ContentBlockParam { Type = "redacted_thinking", Data = p.Data });
                    break;
                case "tool_use":
                    var json = p.Json.Length == 0 ? "{}" : p.Json.ToString();
                    list.Add(new ContentBlockParam
                    {
                        Type = "tool_use", Id = p.Id, Name = p.Name,
                        Input = JsonDocument.Parse(json).RootElement.Clone(),
                    });
                    break;
            }
        }
        return list;
    }
}
