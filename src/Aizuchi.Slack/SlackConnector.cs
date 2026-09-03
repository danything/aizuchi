using Aizuchi.Core;
using Microsoft.Extensions.Logging;

namespace Aizuchi.Slack;

/// <summary>
/// Slack コネクタ。Socket Mode で受けたイベントのうち返すべきものだけを IncomingMessage にして渡す。
/// 反応条件・重複排除・スレッド追従の判定はここに閉じる。
/// </summary>
public sealed class SlackConnector(SlackApi api, SlackOptions options, ILogger log) : IChatConnector
{
    private readonly RecentKeys _seen = new(2000);
    private SocketModeClient? _socket;
    private string _botUserId = "";
    private string? _botId;

    public string Name => "slack";
    public bool Ready => _socket?.Connected ?? false;

    public async Task RunAsync(IMessageHandler handler, CancellationToken ct)
    {
        var me = await api.AuthTest(ct);
        _botUserId = me.UserId!;
        _botId = me.BotId;
        log.LogInformation("Slack にログイン: bot_user={User} bot_id={Bot}", me.UserId, me.BotId);

        _socket = new SocketModeClient(api, options.AppToken, (ev, _, c) => OnEvent(ev, handler, c), log);
        await _socket.RunAsync(ct);
    }

    private async Task OnEvent(SlackEvent ev, IMessageHandler handler, CancellationToken ct)
    {
        var decision = Dispatch.Decide(ev, _botUserId);
        if (decision == Decision.Ignore) return;
        // app_mention と message は同じ発言で両方届く。再送も来る
        if (!_seen.Add($"{ev.Channel}:{ev.Ts}")) return;

        var channel = ev.Channel!;
        var isDm = ev.ChannelType == "im";
        // チャンネルではスレッドで返す。DM のトップレベルはそのまま返す
        var threadTs = ev.ThreadTs ?? (isDm ? null : ev.Ts);

        var history = threadTs is not null
            ? await api.Replies(channel, threadTs, 200, ct)
            : await api.History(channel, 200, ct);

        if (decision == Decision.ReplyIfBotInThread &&
            !history.Any(m => m.User == _botUserId || (_botId is not null && m.BotId == _botId)))
            return;

        var conversation = new SlackConversation(api, channel, threadTs, history, ev, _botUserId, _botId);
        await handler.HandleAsync(new IncomingMessage($"{channel}:{threadTs ?? ev.Ts}", conversation), ct);
    }
}
