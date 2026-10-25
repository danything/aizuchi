using Aizuchi.Core;

public class LogTextTests
{
    [Test]
    [Arguments("anarlog", "anarlog")]
    [Arguments("処理済み abc-123", "処理済み abc-123")]
    [Arguments("a\r\nINFO 偽の行", "a??INFO 偽の行")]
    [Arguments("a\tb\0c\u001bd", "a?b?c?d")]
    [Arguments("a\u2028b\u2029c\u0085d", "a?b?c?d")]
    public async Task 制御文字と行の区切りを置き換える(string text, string expected) =>
        await Assert.That(LogText.Clean(text)).IsEqualTo(expected);

    [Test]
    public async Task null_は空にする() =>
        await Assert.That(LogText.Clean(null)).IsEqualTo("");
}
