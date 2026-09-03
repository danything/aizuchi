using Microsoft.Extensions.Logging;

namespace Aizuchi.Core;

/// <summary>
/// 1 メッセージ = 1 返信の流れ。履歴取得 → 仮投稿 → ストリームを間引いて書き足す → 確定。
/// どのチャットでも、どの LLM でもここは同じ。
/// </summary>
public sealed class Bot(ILlmProvider llm, BotOptions options, ILogger log) : IMessageHandler
{
    public async Task HandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var history = await message.Conversation.HistoryAsync(options.MaxHistory, ct);
        if (history.Count == 0) return;

        var draft = await message.Conversation.BeginReplyAsync(ct);
        var buffer = new System.Text.StringBuilder();
        var throttle = new Throttle(options.UpdateInterval);
        try
        {
            var result = await llm.StreamAsync(new LlmRequest(options.SystemPrompt, history), async delta =>
            {
                buffer.Append(delta);
                if (throttle.Due()) await draft.UpdateAsync(buffer.ToString(), ct);
            }, ct);

            await draft.FinishAsync(FinalText(result), ct);
            log.LogInformation(
                "返信完了 conversation={Conversation} provider={Provider} model={Model} in={In} out={Out} stop={Stop}",
                message.ConversationId, llm.Name, result.Model, result.InputTokens, result.OutputTokens, result.Stop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "返信に失敗 conversation={Conversation}", message.ConversationId);
            try { await draft.FailAsync(ex is LlmException l ? l.Summary : ex.GetType().Name, ct); }
            catch (Exception ex2) { log.LogWarning(ex2, "エラー表示の更新にも失敗"); }
        }
    }

    /// <summary>確定本文。拒絶・途中切れはその旨を添える</summary>
    public static string FinalText(LlmResult result)
    {
        if (result.Stop == StopKind.Refused) return "この依頼には応答できませんでした。";
        var text = result.Text.Trim();
        if (text.Length == 0) return "_(応答が空でした)_";
        if (result.Stop == StopKind.Truncated) text += "\n\n_(出力上限に達したため途中で切れています)_";
        return text;
    }
}

/// <summary>LLM 側の失敗。Summary はチャットに出しても差し支えない短い説明</summary>
public class LlmException(string summary, string detail) : Exception($"{summary}: {detail}")
{
    public string Summary { get; } = summary;
}

/// <summary>一定間隔に間引く。初回は即 true</summary>
public sealed class Throttle(TimeSpan interval, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _last = long.MinValue;

    public bool Due()
    {
        var now = _clock.GetTimestamp();
        if (_last != long.MinValue && _clock.GetElapsedTime(_last, now) < interval) return false;
        _last = now;
        return true;
    }
}
