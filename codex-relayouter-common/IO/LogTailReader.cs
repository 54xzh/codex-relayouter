using System.Text;

namespace codex_bridge.IO;

public static class LogTailReader
{
    public static async Task<string> ReadTailAsync(
        string filePath,
        int maxBytes = 256 * 1024,
        int maxLines = 2000,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath) || maxBytes <= 0)
        {
            return string.Empty;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        if (stream.Length == 0)
        {
            return string.Empty;
        }

        var bytesToRead = (int)Math.Min(maxBytes, stream.Length);
        var startOffset = stream.Length - bytesToRead;
        stream.Seek(startOffset, SeekOrigin.Begin);

        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, bytesToRead - totalRead),
                cancellationToken);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        var normalized = text.Replace("\r\n", "\n");

        if (startOffset > 0)
        {
            var firstNewline = normalized.IndexOf('\n');
            normalized = firstNewline >= 0 ? normalized[(firstNewline + 1)..] : string.Empty;
        }

        normalized = normalized.TrimEnd('\n');

        if (maxLines <= 0)
        {
            return normalized;
        }

        var lines = normalized.Split('\n');
        if (lines.Length <= maxLines)
        {
            return normalized;
        }

        return string.Join("\n", lines[^maxLines..]);
    }
}
