using System.Text;

namespace codex_bridge_server.Bridge;

internal static class CodexCliConfig
{
    private const string ApprovalPolicyKey = "approval_policy";
    private const string SandboxModeKey = "sandbox_mode";

    internal static string GetDefaultConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        }

        return Path.Combine(home, ".codex", "config.toml");
    }

    internal static bool TryLoadApprovalPolicyAndSandboxMode(out string? approvalPolicy, out string? sandboxMode)
    {
        approvalPolicy = null;
        sandboxMode = null;

        var path = GetDefaultConfigPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        try
        {
            var content = ReadAllTextDetectEncoding(path);
            ParseRootSettings(content, out approvalPolicy, out sandboxMode);
            return true;
        }
        catch
        {
            approvalPolicy = null;
            sandboxMode = null;
            return false;
        }
    }

    private static string ReadAllTextDetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void ParseRootSettings(string content, out string? approvalPolicy, out string? sandboxMode)
    {
        approvalPolicy = null;
        sandboxMode = null;

        foreach (var line in EnumerateLines(content))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (TryParseRootStringKeyValue(trimmed, ApprovalPolicyKey, out var parsedApproval))
            {
                approvalPolicy = parsedApproval;
                continue;
            }

            if (TryParseRootStringKeyValue(trimmed, SandboxModeKey, out var parsedSandbox))
            {
                sandboxMode = parsedSandbox;
            }
        }
    }

    private static bool TryParseRootStringKeyValue(string trimmedLine, string key, out string? value)
    {
        value = null;

        if (!trimmedLine.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        var afterKey = trimmedLine.AsSpan(key.Length).TrimStart();
        if (afterKey.Length == 0 || afterKey[0] != '=')
        {
            return false;
        }

        var raw = afterKey.Slice(1).ToString();
        raw = StripTomlComment(raw).Trim();
        if (raw.Length == 0)
        {
            value = null;
            return true;
        }

        value = ParseTomlString(raw);
        return true;
    }

    private static string StripTomlComment(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var isBasicString = false;
        var isLiteralString = false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (!isBasicString && !isLiteralString)
            {
                if (ch == '#')
                {
                    return value.Substring(0, i);
                }

                if (ch == '"')
                {
                    isBasicString = true;
                }
                else if (ch == '\'')
                {
                    isLiteralString = true;
                }

                continue;
            }

            if (isBasicString)
            {
                if (ch == '\\')
                {
                    i++;
                    continue;
                }

                if (ch == '"')
                {
                    isBasicString = false;
                }

                continue;
            }

            if (isLiteralString && ch == '\'')
            {
                isLiteralString = false;
            }
        }

        return value;
    }

    private static string ParseTomlString(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return UnescapeTomlBasicString(raw.Substring(1, raw.Length - 2));
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw.Substring(1, raw.Length - 2);
        }

        return raw;
    }

    private static string UnescapeTomlBasicString(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var isEscape = false;
        foreach (var ch in value)
        {
            if (!isEscape)
            {
                if (ch == '\\')
                {
                    isEscape = true;
                    continue;
                }

                sb.Append(ch);
                continue;
            }

            isEscape = false;
            sb.Append(ch switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => ch
            });
        }

        if (isEscape)
        {
            sb.Append('\\');
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

