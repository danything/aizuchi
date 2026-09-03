using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SlackClaudeBot.Slack;

/// <summary>
/// Socket Mode の接続ループ。apps.connections.open → WebSocket → 封筒を即 ack して
/// events_api だけをハンドラに渡す。切断されたら指数バックオフで張り直す。
/// </summary>
public sealed class SocketModeClient(
    SlackApi api,
    string appToken,
    Func<SlackEvent, string?, CancellationToken, Task> onEvent,
    ILogger log)
{
    public bool Connected { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = await api.OpenSocketUrl(appToken, ct);
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);
                log.LogInformation("Socket Mode に接続");
                backoff = TimeSpan.FromSeconds(1);
                await ReceiveLoop(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Socket Mode の接続が切れた。{Backoff} 秒後に再接続", backoff.TotalSeconds);
            }
            Connected = false;
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close)
            {
                log.LogInformation("Slack 側から WebSocket が閉じられた: {Status}", ws.CloseStatusDescription);
                return;
            }

            var env = JsonSerializer.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length), SlackJson.Default.Envelope);
            if (env is null) continue;

            // 3秒以内に ack しないと再送される。処理より先に返す
            if (env.EnvelopeId is not null)
            {
                var ack = JsonSerializer.SerializeToUtf8Bytes(new Ack { EnvelopeId = env.EnvelopeId }, SlackJson.Default.Ack);
                await ws.SendAsync(ack, WebSocketMessageType.Text, true, ct);
            }

            switch (env.Type)
            {
                case "hello":
                    Connected = true;
                    break;
                case "disconnect":
                    // refresh_requested / link_disabled など。新しい URL で張り直す
                    log.LogInformation("Slack から切断要求: {Reason}", env.Reason);
                    return;
                case "events_api" when env.Payload?.Event is { } ev:
                    _ = Task.Run(async () =>
                    {
                        try { await onEvent(ev, env.Payload.EventId, ct); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { log.LogError(ex, "イベント処理で例外 (event_id={EventId})", env.Payload.EventId); }
                    }, ct);
                    break;
            }
        }
    }
}
