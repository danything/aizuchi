using System.Text;

namespace Aizuchi.Claude;

/// <summary>
/// Server-Sent Events を行単位で食わせ、イベントが閉じた(空行)時点で data を返す。
/// event: 行は Claude の data にも type が入っているので読まない。
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _data = new();
    private bool _hasData;

    /// <returns>イベントが完結したらその data。まだなら null</returns>
    public string? Feed(string line)
    {
        if (line.Length == 0)
        {
            if (!_hasData) return null;
            var data = _data.ToString();
            _data.Clear();
            _hasData = false;
            return data;
        }
        if (line[0] == ':') return null; // コメント(keep-alive)
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

        var value = line.AsSpan(5);
        if (value.Length > 0 && value[0] == ' ') value = value[1..];
        if (_hasData) _data.Append('\n');
        _data.Append(value);
        _hasData = true;
        return null;
    }
}
