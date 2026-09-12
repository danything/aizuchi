namespace Aizuchi.Core;

/// <summary>
/// 直近 N 件のキーを覚える重複判定。Slack の二重配信・再送や、webhook の再送で
/// 同じものを 2 回処理しないために使う。
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
