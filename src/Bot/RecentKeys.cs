namespace SlackClaudeBot.Bot;

/// <summary>
/// 直近 N 件のキーを覚える重複判定。app_mention と message の二重配信や
/// Slack の再送で同じメッセージに 2 回答えないために使う。
/// </summary>
public sealed class RecentKeys(int capacity)
{
    private readonly HashSet<string> _set = [];
    private readonly Queue<string> _order = new();
    private readonly Lock _lock = new();

    /// <returns>初見なら true。既に見ていたら false</returns>
    public bool Add(string key)
    {
        lock (_lock)
        {
            if (!_set.Add(key)) return false;
            _order.Enqueue(key);
            while (_order.Count > capacity) _set.Remove(_order.Dequeue());
            return true;
        }
    }
}
