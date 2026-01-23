// CodexCliInfo：读取并缓存本机 codex CLI 版本号（用于写入 session_meta.cli_version）。
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class CodexCliInfo
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IOptions<CodexOptions> _options;
    private readonly ILogger<CodexCliInfo> _logger;
    private readonly object _gate = new();
    private string? _cachedCliVersion;

    public CodexCliInfo(IOptions<CodexOptions> options, ILogger<CodexCliInfo> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string GetCliVersion()
    {
        lock (_gate)
        {
            _cachedCliVersion ??= TryReadCliVersion() ?? "unknown";
            return _cachedCliVersion;
        }
    }

    private string? TryReadCliVersion()
    {
        try
        {
            var invocation = CodexRunner.ResolveCodexInvocation(_options.Value.Executable);

            var startInfo = new ProcessStartInfo
            {
                FileName = invocation.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                CreateNoWindow = true,
            };

            foreach (var arg in invocation.PrefixArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.ArgumentList.Add("--version");

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(milliseconds: 3_000);

            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var version = ParseCliVersion(text);
            if (string.IsNullOrWhiteSpace(version))
            {
                _logger.LogWarning("无法解析 codex --version 输出: {Output}", text);
                return null;
            }

            return version.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 codex CLI 版本失败");
            return null;
        }
    }

    private static string? ParseCliVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var reader = new StringReader(text);
        var firstLine = reader.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        var parts = firstLine.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return parts[^1];
    }
}

