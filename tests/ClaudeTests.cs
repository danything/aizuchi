using System.Text.Json;
using Aizuchi.Claude;
using System.Threading.Tasks;

public class ClaudeTests
{
    [Test]
    public async Task SSEは空行でイベントが閉じ_コメントとevent行は無視()
    {
        var p = new SseParser();
        await Assert.That(p.Feed("event: content_block_delta")).IsNull();
        await Assert.That(p.Feed(": keep-alive")).IsNull();
        await Assert.That(p.Feed("""data: {"a":1}""")).IsNull();
        await Assert.That(p.Feed("")).IsEqualTo("""{"a":1}""");
        await Assert.That(p.Feed("")).IsNull(); // 連続空行は何も返さない
    }

    [Test]
    public async Task 複数dataは改行で結合される()
    {
        var p = new SseParser();
        p.Feed("data: 1");
        p.Feed("data:2");
        await Assert.That(p.Feed("")).IsEqualTo("1\n2");
    }

    [Test]
    public async Task ストリームイベントを読める()
    {
        var delta = JsonSerializer.Deserialize(
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"こん"}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(delta.Delta!.Type).IsEqualTo("text_delta");
        await Assert.That(delta.Delta.Text).IsEqualTo("こん");

        var stop = JsonSerializer.Deserialize(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":42}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(stop.Delta!.StopReason).IsEqualTo("end_turn");
        await Assert.That(stop.Usage!.OutputTokens).IsEqualTo(42);

        var start = JsonSerializer.Deserialize(
            """{"type":"message_start","message":{"id":"m1","model":"claude-opus-5","usage":{"input_tokens":10,"cache_read_input_tokens":0}}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(start.Message!.Model).IsEqualTo("claude-opus-5");
        await Assert.That(start.Message.Usage!.InputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task リクエスト本文は必要な項目だけsnake_caseで出る()
    {
        var req = new MessagesRequest
        {
            Model = "claude-opus-5", MaxTokens = 16000, System = "sys",
            Messages = [new() { Role = "user", Content = "hi" }],
            OutputConfig = new() { Effort = "high" },
            Fallbacks = "default",
        };
        var json = JsonSerializer.Serialize(req, ClaudeJson.Default.MessagesRequest);
        await Assert.That(json).IsEqualTo("""{"model":"claude-opus-5","max_tokens":16000,"stream":true,"system":"sys","messages":[{"role":"user","content":"hi"}],"output_config":{"effort":"high"},"fallbacks":"default"}""");

        var minimal = JsonSerializer.Serialize(
            new MessagesRequest { Model = "m", MaxTokens = 1, Messages = [] }, ClaudeJson.Default.MessagesRequest);
        await Assert.That(minimal).DoesNotContain("output_config");
        await Assert.That(minimal).DoesNotContain("fallbacks");
        await Assert.That(minimal).DoesNotContain("thinking");
    }
}