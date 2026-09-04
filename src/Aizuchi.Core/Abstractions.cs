using System.Text.Json;

namespace Aizuchi.Core;

/// <summary>LLM に渡す 1 発言。Role は "user" か "assistant"</summary>
public sealed record ChatMessage(string Role, string Content);

public enum StopKind
{
    /// <summary>普通に言い終えた</summary>
    Completed,
    /// <summary>出力上限で切れた</summary>
    Truncated,
    /// <summary>安全側で拒絶された</summary>
    Refused,
    /// <summary>ツール往復の上限に達して打ち切った</summary>
    ToolLimited,
}

/// <summary>LLM から呼べる道具。スキーマは JSON Schema の文字列で持つ(AOT でリフレクションを避ける)</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    /// <summary>input の JSON Schema(オブジェクト)</summary>
    string InputSchemaJson { get; }
    Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct);
}

public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>道具のまとまり(GitHub など)。system prompt に足す説明と道具の一覧</summary>
public interface IToolPack
{
    string Name { get; }
    /// <summary>この道具群の使い方。system prompt の末尾に足される</summary>
    string PromptSection { get; }
    IReadOnlyList<ITool> Tools { get; }
}

public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ITool> Tools);

public sealed record LlmResult(
    string Text,
    StopKind Stop,
    string? Model,
    long InputTokens,
    long OutputTokens,
    int ToolCalls = 0);

/// <summary>
/// Claude / OpenAI / ローカル LLM などの差し替え点。テキストをストリーミングで返す。
/// ツールが渡されたら、呼び出し → 結果を返す → 続きを生成、の往復もプロバイダの中で済ませる。
/// </summary>
public interface ILlmProvider
{
    string Name { get; }

    /// <param name="onText">増分テキストが届くたびに呼ばれる</param>
    Task<LlmResult> StreamAsync(LlmRequest request, Func<string, Task> onText, CancellationToken ct);
}

/// <summary>Slack / Mattermost / Discord などの差し替え点。接続を維持して、返すべきメッセージだけを handler に渡す</summary>
public interface IChatConnector
{
    string Name { get; }

    /// <summary>readiness 用。接続が確立していれば true</summary>
    bool Ready { get; }

    /// <summary>止められるまで動き続ける。切断時の再接続もこの中で面倒を見る</summary>
    Task RunAsync(IMessageHandler handler, CancellationToken ct);
}

public interface IMessageHandler
{
    Task HandleAsync(IncomingMessage message, CancellationToken ct);
}

/// <summary>コネクタが「これには返す」と判定したメッセージ</summary>
/// <param name="ConversationId">ログ用の会話キー(例: C123:1700000000.000100)</param>
/// <param name="Scope">チャンネル単位の記憶を分けるキー(例: Slack のチャンネル ID)</param>
/// <param name="Text">発火元の本文。メンション除去・エンティティ復元済み</param>
public sealed record IncomingMessage(
    string ConversationId,
    string Scope,
    string Text,
    IConversation Conversation);

/// <summary>返信先の会話。履歴の取り出しと返信の出し方はコネクタが知っている</summary>
public interface IConversation
{
    /// <summary>LLM に渡せる形に整えた履歴。末尾が発火元の user 発言になる</summary>
    Task<IReadOnlyList<ChatMessage>> HistoryAsync(int maxMessages, CancellationToken ct);

    /// <summary>「考えています」相当の仮メッセージを出して、後から書き換えられる下書きを返す</summary>
    Task<IReplyDraft> BeginReplyAsync(CancellationToken ct);
}

/// <summary>ストリーミングで書き換えていく返信。本文は Markdown で受け取り、変換はコネクタ側で行う</summary>
public interface IReplyDraft
{
    /// <summary>途中経過。失敗しても投げない(最終更新で取り戻す)</summary>
    Task UpdateAsync(string markdown, CancellationToken ct);

    Task FinishAsync(string markdown, CancellationToken ct);

    Task FailAsync(string reason, CancellationToken ct);
}
