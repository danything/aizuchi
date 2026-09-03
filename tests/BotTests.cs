using TUnit.Assertions.Enums;
using Aizuchi.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Tasks;

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

    [Test]
    public async Task ストリームを流し込んで確定する()
    {
        var provider = new FakeProvider(["こん", "にちは"]);
        var conv = new FakeConversation(History);
        await new Bot(provider, Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), CancellationToken.None);

        await Assert.That(provider.LastRequest!.SystemPrompt).IsEqualTo("sys");
        await Assert.That(provider.LastRequest.Messages).IsEquivalentTo(History, CollectionOrdering.Matching);
        await Assert.That(conv.Updates).IsEquivalentTo(["こん", "こんにちは"], CollectionOrdering.Matching); // 間隔 0 なので毎回更新
        await Assert.That(conv.Final).IsEqualTo("こんにちは");
        await Assert.That(conv.Failure).IsNull();
    }

    [Test]
    public async Task 履歴が空なら何もしない()
    {
        var conv = new FakeConversation([]);
        await new Bot(new FakeProvider(["x"]), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), CancellationToken.None);
        await Assert.That(conv.Began).IsFalse();
    }

    [Test]
    public async Task 途中切れと拒絶は文言を添える()
    {
        var truncated = new FakeConversation(History);
        await new Bot(new FakeProvider(["長い"], StopKind.Truncated), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", truncated), CancellationToken.None);
        await Assert.That(truncated.Final).StartsWith("長い\n\n_(出力上限");

        var refused = new FakeConversation(History);
        await new Bot(new FakeProvider(["途中"], StopKind.Refused), Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", refused), CancellationToken.None);
        await Assert.That(refused.Final).IsEqualTo("この依頼には応答できませんでした。");
    }

    [Test]
    public async Task 失敗したら下書きにエラーを書く()
    {
        var conv = new FakeConversation(History);
        var provider = new FakeProvider(["a"], fail: new LlmException("Claude API HTTP 529", "overloaded"));
        await new Bot(provider, Options, NullLogger.Instance).HandleAsync(new IncomingMessage("c", conv), CancellationToken.None);
        await Assert.That(conv.Final).IsNull();
        await Assert.That(conv.Failure).IsEqualTo("Claude API HTTP 529");
    }

    private sealed class FakeClock : TimeProvider
    {
        public long Now;
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => 1000; // 1 tick = 1ms
    }

    [Test]
    public async Task 間引きは初回即通し以後は間隔ごと()
    {
        var clock = new FakeClock();
        var t = new Throttle(TimeSpan.FromMilliseconds(100), clock);
        await Assert.That(t.Due()).IsTrue();
        clock.Now = 50; await Assert.That(t.Due()).IsFalse();
        clock.Now = 100; await Assert.That(t.Due()).IsTrue();
        clock.Now = 150; await Assert.That(t.Due()).IsFalse();
    }

    [Test]
    public async Task stop_reasonの対応()
    {
        await Assert.That(Aizuchi.Claude.ClaudeProvider.ToStopKind("max_tokens")).IsEqualTo(StopKind.Truncated);
        await Assert.That(Aizuchi.Claude.ClaudeProvider.ToStopKind("refusal")).IsEqualTo(StopKind.Refused);
        await Assert.That(Aizuchi.Claude.ClaudeProvider.ToStopKind("end_turn")).IsEqualTo(StopKind.Completed);
        await Assert.That(Aizuchi.Claude.ClaudeProvider.ToStopKind(null)).IsEqualTo(StopKind.Completed);
    }
}