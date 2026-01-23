using System.Text;
using codex_bridge.IO;

namespace codex_bridge_common.Tests;

public sealed class LogTailReaderTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task ReadTailAsync_FileMissing_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "missing.log");
        var text = await LogTailReader.ReadTailAsync(missing);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task ReadTailAsync_MaxLines_TakesLastLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "1\r\n2\r\n3\r\n", Utf8NoBom);
            var text = await LogTailReader.ReadTailAsync(path, maxBytes: 1024, maxLines: 2);
            Assert.Equal("2\n3", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadTailAsync_MaxLinesLessOrEqualZero_ReturnsAllTail()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "a\r\nb\r\nc\r\n", Utf8NoBom);
            var text = await LogTailReader.ReadTailAsync(path, maxBytes: 1024, maxLines: 0);
            Assert.Equal("a\nb\nc", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadTailAsync_WhenCutFromMiddle_DropsPartialLeadingLine()
    {
        var path = Path.GetTempFileName();
        try
        {
            var longLine = new string('x', 10_000);
            File.WriteAllText(path, $"{longLine}\nTAIL1\nTAIL2\n", Utf8NoBom);

            var text = await LogTailReader.ReadTailAsync(path, maxBytes: 200, maxLines: 100);
            Assert.Equal("TAIL1\nTAIL2", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

