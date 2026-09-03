using Aizuchi.Core;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>コネクタもプロバイダも偽物にして、返信の流れ本体だけを確かめる</summary>
public class BotTests
{
    private sealed class FakeProvider(string[] chunks, StopKind stop = StopKind.Completed, Exception? fail = null) : ILlmProvider
    {
        public LlmRequest? LastRequest;
        public string Name => "fake";
        public async Task<LlmResult> StreamAsync(LlmRequest request, Func<string, Task> onText, CancellationToken ct)
        {
            LastRequest = request;
            foreach (var c in chunks) await onText(c);
            if (fail is not null) throw fail;
            return new LlmResult(string.Concat(chunks), stop, "fake-1", 10, 20);
        }
    }

    private sealed class FakeConversation(IReadOnlyList<ChatMessage> history) : IConversation, IReplyDraft
    {
        public readonly List<string> Updates = [];
        public string? Final;
        public string? Failure;
        public bool Began;

        public Task<IReadOnlyList<ChatMessage>> HistoryAsync(int max, CancellationToken ct) => Task.FromResult(history);
        public Task<IReplyDraft> BeginReplyAsync(CancellationToken ct) { Began = true; return Task.FromResult<IReplyDraft>(this); }
        public Task UpdateAsync(string markdown, CancellationToken ct) { Updates.Add(markdown); return Task.CompletedTask; }
        public Task FinishAsync(string markdown, CancellationToken ct) { Final = markdown; return Task.CompletedTask; }
        public Task FailAsync(string reason, CancellationToken ct) { Failure = reason; return Task.CompletedTask; }
    }

    private static readonly BotOptions Options = new("sys", 50, TimeSpan.Zero);
    private static readonly IReadOnlyList<ChatMessage> History = [new("user", "hi")];

    [Fact]
    public async Task ストリームを流し込んで確定する()
    {
        var provider = new FakeProvider(["こん", "にちは"]);
        var conv = new FakeConversation(History);
        await new Bot(provider, Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), TestContext.Current.CancellationToken);

        Assert.Equal("sys", provider.LastRequest!.SystemPrompt);
        Assert.Equal(History, provider.LastRequest.Messages);
        Assert.Equal(["こん", "こんにちは"], conv.Updates); // 間隔 0 なので毎回更新
        Assert.Equal("こんにちは", conv.Final);
        Assert.Null(conv.Failure);
    }

    [Fact]
    public async Task 履歴が空なら何もしない()
    {
        var conv = new FakeConversation([]);
        await new Bot(new FakeProvider(["x"]), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), TestContext.Current.CancellationToken);
        Assert.False(conv.Began);
    }

    [Fact]
    public async Task 途中切れと拒絶は文言を添える()
    {
        var truncated = new FakeConversation(History);
        await new Bot(new FakeProvider(["長い"], StopKind.Truncated), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", truncated), TestContext.Current.CancellationToken);
        Assert.StartsWith("長い\n\n_(出力上限", truncated.Final);

        var refused = new FakeConversation(History);
        await new Bot(new FakeProvider(["途中"], StopKind.Refused), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", refused), TestContext.Current.CancellationToken);
        Assert.Equal("この依頼には応答できませんでした。", refused.Final);
    }

    [Fact]
    public async Task 失敗したら下書きにエラーを書く()
    {
        var conv = new FakeConversation(History);
        var provider = new FakeProvider(["a"], fail: new LlmException("Claude API HTTP 529", "overloaded"));
        await new Bot(provider, Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), TestContext.Current.CancellationToken);
        Assert.Null(conv.Final);
        Assert.Equal("Claude API HTTP 529", conv.Failure);
    }

    private sealed class FakeClock : TimeProvider
    {
        public long Now;
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => 1000; // 1 tick = 1ms
    }

    [Fact]
    public void 間引きは初回即通し以後は間隔ごと()
    {
        var clock = new FakeClock();
        var t = new Throttle(TimeSpan.FromMilliseconds(100), clock);
        Assert.True(t.Due());
        clock.Now = 50; Assert.False(t.Due());
        clock.Now = 100; Assert.True(t.Due());
        clock.Now = 150; Assert.False(t.Due());
    }

    [Fact]
    public void stop_reasonの対応()
    {
        Assert.Equal(StopKind.Truncated, Aizuchi.Claude.ClaudeProvider.ToStopKind("max_tokens"));
        Assert.Equal(StopKind.Refused, Aizuchi.Claude.ClaudeProvider.ToStopKind("refusal"));
        Assert.Equal(StopKind.Completed, Aizuchi.Claude.ClaudeProvider.ToStopKind("end_turn"));
        Assert.Equal(StopKind.Completed, Aizuchi.Claude.ClaudeProvider.ToStopKind(null));
    }
}
