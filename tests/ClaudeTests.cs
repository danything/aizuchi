using System.Text.Json;
using Aizuchi.Claude;

public class ClaudeTests
{
    [Fact]
    public void SSEは空行でイベントが閉じ_コメントとevent行は無視()
    {
        var p = new SseParser();
        Assert.Null(p.Feed("event: content_block_delta"));
        Assert.Null(p.Feed(": keep-alive"));
        Assert.Null(p.Feed("""data: {"a":1}"""));
        Assert.Equal("""{"a":1}""", p.Feed(""));
        Assert.Null(p.Feed("")); // 連続空行は何も返さない
    }

    [Fact]
    public void 複数dataは改行で結合される()
    {
        var p = new SseParser();
        p.Feed("data: 1");
        p.Feed("data:2");
        Assert.Equal("1\n2", p.Feed(""));
    }

    [Fact]
    public void ストリームイベントを読める()
    {
        var delta = JsonSerializer.Deserialize(
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"こん"}}""",
            ClaudeJson.Default.StreamEvent)!;
        Assert.Equal("text_delta", delta.Delta!.Type);
        Assert.Equal("こん", delta.Delta.Text);

        var stop = JsonSerializer.Deserialize(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":42}}""",
            ClaudeJson.Default.StreamEvent)!;
        Assert.Equal("end_turn", stop.Delta!.StopReason);
        Assert.Equal(42, stop.Usage!.OutputTokens);

        var start = JsonSerializer.Deserialize(
            """{"type":"message_start","message":{"id":"m1","model":"claude-opus-5","usage":{"input_tokens":10,"cache_read_input_tokens":0}}}""",
            ClaudeJson.Default.StreamEvent)!;
        Assert.Equal("claude-opus-5", start.Message!.Model);
        Assert.Equal(10, start.Message.Usage!.InputTokens);
    }

    [Fact]
    public void リクエスト本文は必要な項目だけsnake_caseで出る()
    {
        var req = new MessagesRequest
        {
            Model = "claude-opus-5", MaxTokens = 16000, System = "sys",
            Messages = [new() { Role = "user", Content = "hi" }],
            OutputConfig = new() { Effort = "high" },
            Fallbacks = "default",
        };
        var json = JsonSerializer.Serialize(req, ClaudeJson.Default.MessagesRequest);
        Assert.Equal(
            """{"model":"claude-opus-5","max_tokens":16000,"stream":true,"system":"sys","messages":[{"role":"user","content":"hi"}],"output_config":{"effort":"high"},"fallbacks":"default"}""",
            json);

        var minimal = JsonSerializer.Serialize(
            new MessagesRequest { Model = "m", MaxTokens = 1, Messages = [] }, ClaudeJson.Default.MessagesRequest);
        Assert.DoesNotContain("output_config", minimal);
        Assert.DoesNotContain("fallbacks", minimal);
        Assert.DoesNotContain("thinking", minimal);
    }
}
