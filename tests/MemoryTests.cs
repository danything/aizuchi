using System.Text.Json;
using Aizuchi.Core;
using Microsoft.Extensions.Logging.Abstractions;

public class MemoryTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("aizuchi-mem-").FullName;

    [Test]
    public async Task ファイルに書いて読み戻せる_無ければ空()
    {
        var store = new FileMemoryStore(TempDir());
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("");
        await store.WriteAsync("shared", "- a\n", CancellationToken.None);
        await store.WriteAsync("C0123/../x", "- ch\n", CancellationToken.None);
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("- a\n");
        await Assert.That(await store.ReadAsync("C0123/../x", CancellationToken.None)).IsEqualTo("- ch\n");
        await Assert.That(store.PathFor("C0123/../x")).EndsWith(Path.Combine("channels", "C0123____x.md"));
    }

    [Test]
    public async Task 追記は末尾に足し_上限を超えたら断る()
    {
        var store = new FileMemoryStore(TempDir());
        var tools = MemoryTools.For(store, "C1", 30);
        var append = tools.Single(t => t.Name == "memory_append");

        var r1 = await append.InvokeAsync(JsonDocument.Parse("""{"scope":"shared","text":"- 社名は danything"}""").RootElement, CancellationToken.None);
        await Assert.That(r1.IsError).IsFalse();
        var r2 = await append.InvokeAsync(JsonDocument.Parse("""{"scope":"channel","text":"- ch"}""").RootElement, CancellationToken.None);
        await Assert.That(r2.IsError).IsFalse();
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("- 社名は danything\n");
        await Assert.That(await store.ReadAsync("C1", CancellationToken.None)).IsEqualTo("- ch\n");

        var r3 = await append.InvokeAsync(JsonDocument.Parse("""{"scope":"shared","text":"- とても長い追記でいっぱいいっぱいになる文章"}""").RootElement, CancellationToken.None);
        await Assert.That(r3.IsError).IsTrue();
        await Assert.That(r3.Content).Contains("memory_replace");
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("- 社名は danything\n");

        var empty = await append.InvokeAsync(JsonDocument.Parse("""{"scope":"shared","text":""}""").RootElement, CancellationToken.None);
        await Assert.That(empty.IsError).IsTrue();
    }

    [Test]
    public async Task 置き換えと全消去()
    {
        var store = new FileMemoryStore(TempDir());
        var replace = MemoryTools.For(store, "C1", 100).Single(t => t.Name == "memory_replace");
        await replace.InvokeAsync(JsonDocument.Parse("""{"scope":"shared","content":"- x\n- y"}""").RootElement, CancellationToken.None);
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("- x\n- y\n");
        var r = await replace.InvokeAsync(JsonDocument.Parse("""{"scope":"shared","content":""}""").RootElement, CancellationToken.None);
        await Assert.That(r.Content).Contains("消去");
        await Assert.That(await store.ReadAsync("shared", CancellationToken.None)).IsEqualTo("");
    }

    [Test]
    public async Task ツールのスキーマは正しいJSON()
    {
        foreach (var t in MemoryTools.For(new FileMemoryStore(TempDir()), "C1", 100))
        {
            var schema = JsonDocument.Parse(t.InputSchemaJson).RootElement;
            await Assert.That(schema.GetProperty("type").GetString()).IsEqualTo("object");
            await Assert.That(schema.GetProperty("properties").GetProperty("scope").GetProperty("enum").GetArrayLength()).IsEqualTo(2);
        }
    }

    [Test]
    public async Task system_promptの記憶セクション()
    {
        var section = MemoryPrompt.Section(new MemorySnapshot("- 用語: denpa = 録画アプリ\n", ""), 8000);
        await Assert.That(section).Contains("## shared\n- 用語: denpa = 録画アプリ");
        await Assert.That(section).Contains("## channel\n(まだ何もありません)");
        await Assert.That(section).Contains("8000 文字");
    }

    [Test]
    [Arguments("memory", false, null)]
    [Arguments("記憶", false, null)]
    [Arguments("memory channel", true, null)]
    [Arguments("記憶 チャンネル", true, null)]
    [Arguments("Memory ```\n- a\n- b\n```", false, "- a\n- b")]
    [Arguments("memory channel\n```md\n- c\n```", true, "- c")]
    [Arguments("memory ```\n```", false, "")]
    public async Task 手動コマンドの解釈(string text, bool isChannel, string? replacement)
    {
        await Assert.That(MemoryCommand.TryParse(text, out var cmd)).IsTrue();
        await Assert.That(cmd.IsChannel).IsEqualTo(isChannel);
        await Assert.That(cmd.Replacement).IsEqualTo(replacement);
    }

    [Test]
    [Arguments("memory とは何ですか")]
    [Arguments("記憶力を上げるには")]
    [Arguments("こんにちは")]
    public async Task 手動コマンドでないもの(string text)
    {
        await Assert.That(MemoryCommand.TryParse(text, out _)).IsFalse();
    }

    private sealed class SpyProvider : ILlmProvider
    {
        public LlmRequest? Last;
        public string Name => "spy";
        public Task<LlmResult> StreamAsync(LlmRequest request, Func<string, Task> onText, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new LlmResult("ok", StopKind.Completed, "m", 1, 1));
        }
    }

    private sealed class FakeConversation : IConversation, IReplyDraft
    {
        public string? Final;
        public Task<IReadOnlyList<ChatMessage>> HistoryAsync(int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([new("user", "hi")]);
        public Task<IReplyDraft> BeginReplyAsync(CancellationToken ct) => Task.FromResult<IReplyDraft>(this);
        public Task UpdateAsync(string markdown, CancellationToken ct) => Task.CompletedTask;
        public Task FinishAsync(string markdown, CancellationToken ct) { Final = markdown; return Task.CompletedTask; }
        public Task FailAsync(string reason, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task Botは記憶をsystem_promptに差し込みツールを渡す()
    {
        var store = new FileMemoryStore(TempDir());
        await store.WriteAsync("shared", "- 社名は danything\n", CancellationToken.None);
        var provider = new SpyProvider();
        var bot = new Bot(provider, new BotOptions("base", 50, TimeSpan.Zero, "x", 8000, 0), store, NullLogger.Instance);
        var conv = new FakeConversation();

        await bot.HandleAsync(new IncomingMessage("c", "C1", "教えて", conv), CancellationToken.None);

        await Assert.That(provider.Last!.SystemPrompt).StartsWith("base");
        await Assert.That(provider.Last.SystemPrompt).Contains("- 社名は danything");
        await Assert.That(provider.Last.Tools.Select(t => t.Name)).IsEquivalentTo(["memory_append", "memory_replace"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(conv.Final).IsEqualTo("ok");
    }

    [Test]
    public async Task 手動コマンドはLLMを通さず表示と置き換えをする()
    {
        var store = new FileMemoryStore(TempDir());
        var provider = new SpyProvider();
        var bot = new Bot(provider, new BotOptions("base", 50, TimeSpan.Zero, "x", 8000, 0), store, NullLogger.Instance);

        var show = new FakeConversation();
        await bot.HandleAsync(new IncomingMessage("c", "C1", "memory", show), CancellationToken.None);
        await Assert.That(provider.Last).IsNull();
        await Assert.That(show.Final).Contains("まだ何もありません");

        var replace = new FakeConversation();
        await bot.HandleAsync(new IncomingMessage("c", "C1", "memory channel ```\n- ここは開発チャンネル\n```", replace), CancellationToken.None);
        await Assert.That(await store.ReadAsync("C1", CancellationToken.None)).IsEqualTo("- ここは開発チャンネル\n");
        await Assert.That(replace.Final).Contains("- ここは開発チャンネル");
        await Assert.That(provider.Last).IsNull();
    }
}
