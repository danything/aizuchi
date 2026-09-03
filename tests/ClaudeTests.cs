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
            Messages = [MessageParam.Text("user", "hi")],
            Tools = [new ToolParam { Name = "t", Description = "d", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement }],
            OutputConfig = new() { Effort = "high" },
            Fallbacks = "default",
        };
        var json = JsonSerializer.Serialize(req, ClaudeJson.Default.MessagesRequest);
        await Assert.That(json).IsEqualTo(
            """{"model":"claude-opus-5","max_tokens":16000,"stream":true,"system":"sys","messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}],"tools":[{"name":"t","description":"d","input_schema":{"type":"object"}}],"output_config":{"effort":"high"},"fallbacks":"default"}""");

        var minimal = JsonSerializer.Serialize(
            new MessagesRequest { Model = "m", MaxTokens = 1, Messages = [] }, ClaudeJson.Default.MessagesRequest);
        await Assert.That(minimal).DoesNotContain("output_config");
        await Assert.That(minimal).DoesNotContain("fallbacks");
        await Assert.That(minimal).DoesNotContain("tools");
        await Assert.That(minimal).DoesNotContain("thinking");
    }

    [Test]
    public async Task ツール呼び出しのブロックを組み立てて次の要求に返せる形にする()
    {
        var b = new BlockBuilder();
        b.Start(0, new StreamContentBlock { Type = "thinking", Thinking = "" });
        b.Delta(0, new StreamDelta { Type = "signature_delta", Signature = "sig==" });
        b.Start(1, new StreamContentBlock { Type = "text", Text = "" });
        b.Delta(1, new StreamDelta { Type = "text_delta", Text = "覚え" });
        b.Delta(1, new StreamDelta { Type = "text_delta", Text = "ます" });
        b.Start(2, new StreamContentBlock { Type = "tool_use", Id = "toolu_1", Name = "memory_append" });
        b.Delta(2, new StreamDelta { Type = "input_json_delta", PartialJson = """{"scope":"sha""" });
        b.Delta(2, new StreamDelta { Type = "input_json_delta", PartialJson = """red","text":"x"}""" });
        var blocks = b.Finish();

        await Assert.That(blocks.Count).IsEqualTo(3);
        await Assert.That(blocks[0].Type).IsEqualTo("thinking");
        await Assert.That(blocks[0].Signature).IsEqualTo("sig==");
        await Assert.That(blocks[1].Text).IsEqualTo("覚えます");
        await Assert.That(blocks[2].Name).IsEqualTo("memory_append");
        await Assert.That(blocks[2].Input!.Value.GetProperty("scope").GetString()).IsEqualTo("shared");

        // 返すときの形: thinking は署名付き、tool_use は input がオブジェクト
        var json = JsonSerializer.Serialize(new MessageParam { Role = "assistant", Content = blocks }, ClaudeJson.Default.MessagesRequest.Options.GetTypeInfo(typeof(MessageParam)));
        await Assert.That(json).Contains("""{"type":"thinking","thinking":"","signature":"sig=="}""");
        await Assert.That(json).Contains("""{"type":"tool_use","id":"toolu_1","name":"memory_append","input":{"scope":"shared","text":"x"}}""");
        await Assert.That(json).DoesNotContain("is_error");

        var result = JsonSerializer.Serialize(
            new ContentBlockParam { Type = "tool_result", ToolUseId = "toolu_1", Content = "ok", IsError = true },
            ClaudeJson.Default.MessagesRequest.Options.GetTypeInfo(typeof(ContentBlockParam)));
        await Assert.That(result).IsEqualTo("""{"type":"tool_result","tool_use_id":"toolu_1","content":"ok","is_error":true}""");
    }

    [Test]
    public async Task tool_useのストリームイベントを読める()
    {
        var start = JsonSerializer.Deserialize(
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"memory_append","input":{}}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(start.ContentBlock!.Name).IsEqualTo("memory_append");
        var delta = JsonSerializer.Deserialize(
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"a\":"}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(delta.Delta!.PartialJson).IsEqualTo("{\"a\":");
        var stop = JsonSerializer.Deserialize(
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""",
            ClaudeJson.Default.StreamEvent)!;
        await Assert.That(stop.Delta!.StopReason).IsEqualTo("tool_use");
    }
}
