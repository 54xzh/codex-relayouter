using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace codex_bridge.State;

internal static class CodexCliConfig
{
    private const string ModelKey = "model";
    private const string ModelReasoningEffortKey = "model_reasoning_effort";
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

    internal static bool TryLoadModelAndReasoningEffort(out string? model, out string? modelReasoningEffort)
    {
        model = null;
        modelReasoningEffort = null;

        return TryLoadModelReasoningEffortApprovalPolicyAndSandboxMode(
            out model,
            out modelReasoningEffort,
            out _,
            out _);
    }

    internal static bool TryLoadApprovalPolicyAndSandboxMode(out string? approvalPolicy, out string? sandboxMode)
    {
        approvalPolicy = null;
        sandboxMode = null;

        return TryLoadModelReasoningEffortApprovalPolicyAndSandboxMode(
            out _,
            out _,
            out approvalPolicy,
            out sandboxMode);
    }

    internal static bool TryLoadModelReasoningEffortApprovalPolicyAndSandboxMode(
        out string? model,
        out string? modelReasoningEffort,
        out string? approvalPolicy,
        out string? sandboxMode)
    {
        model = null;
        modelReasoningEffort = null;
        approvalPolicy = null;
        sandboxMode = null;

        return TryLoadRootSettings(out model, out modelReasoningEffort, out approvalPolicy, out sandboxMode);
    }

    private static bool TryLoadRootSettings(
        out string? model,
        out string? modelReasoningEffort,
        out string? approvalPolicy,
        out string? sandboxMode)
    {
        model = null;
        modelReasoningEffort = null;
        approvalPolicy = null;
        sandboxMode = null;

        var path = GetDefaultConfigPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        try
        {
            var loaded = ReadAllTextDetectEncoding(path);
            ParseRootSettings(loaded, out model, out modelReasoningEffort, out approvalPolicy, out sandboxMode);
            return true;
        }
        catch
        {
            model = null;
            modelReasoningEffort = null;
            approvalPolicy = null;
            sandboxMode = null;
            return false;
        }
    }

    internal static bool TryUpdateModelAndReasoningEffort(string? model, string? modelReasoningEffort, out string? error)
    {
        error = null;

        try
        {
            var configPath = GetDefaultConfigPath();
            if (string.IsNullOrWhiteSpace(configPath))
            {
                error = "无法定位用户目录，无法更新 config.toml";
                return false;
            }

            var directory = Path.GetDirectoryName(configPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = $"无效配置路径: {configPath}";
                return false;
            }

            Directory.CreateDirectory(directory);

            var original = File.Exists(configPath) ? ReadAllTextDetectEncoding(configPath) : string.Empty;
            var updated = UpsertRootModelAndEffort(original, model, modelReasoningEffort, out var changed);
            if (!changed)
            {
                return true;
            }

            WriteAllTextUtf8NoBomAtomic(configPath, updated);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ReadAllTextDetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteAllTextUtf8NoBomAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"无效配置路径: {path}");
        }

        var tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static void ParseRootSettings(
        string content,
        out string? model,
        out string? modelReasoningEffort,
        out string? approvalPolicy,
        out string? sandboxMode)
    {
        model = null;
        modelReasoningEffort = null;
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

            if (TryParseRootStringKeyValue(trimmed, ModelKey, out var parsedModel))
            {
                model = parsedModel;
                continue;
            }

            if (TryParseRootStringKeyValue(trimmed, ModelReasoningEffortKey, out var parsedEffort))
            {
                modelReasoningEffort = parsedEffort;
                continue;
            }

            if (TryParseRootStringKeyValue(trimmed, ApprovalPolicyKey, out var parsedApprovalPolicy))
            {
                approvalPolicy = parsedApprovalPolicy;
                continue;
            }

            if (TryParseRootStringKeyValue(trimmed, SandboxModeKey, out var parsedSandboxMode))
            {
                sandboxMode = parsedSandboxMode;
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

    private static string StripTomlComment(string text)
    {
        var isInDoubleQuotes = false;
        var isInSingleQuotes = false;
        var isEscape = false;

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (isEscape)
            {
                isEscape = false;
                sb.Append(ch);
                continue;
            }

            if (isInDoubleQuotes && ch == '\\')
            {
                isEscape = true;
                sb.Append(ch);
                continue;
            }

            if (!isInSingleQuotes && ch == '"')
            {
                isInDoubleQuotes = !isInDoubleQuotes;
                sb.Append(ch);
                continue;
            }

            if (!isInDoubleQuotes && ch == '\'')
            {
                isInSingleQuotes = !isInSingleQuotes;
                sb.Append(ch);
                continue;
            }

            if (!isInDoubleQuotes && !isInSingleQuotes && ch == '#')
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString();
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

    private static string EscapeTomlBasicString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string UpsertRootModelAndEffort(string original, string? model, string? modelReasoningEffort, out bool changed)
    {
        var hasCrLf = original.Contains("\r\n", StringComparison.Ordinal);
        var newline = hasCrLf ? "\r\n" : "\n";
        var hadTrailingNewline = original.EndsWith("\n", StringComparison.Ordinal);

        var lines = new List<string>(EnumerateLines(original));
        changed = false;

        UpsertRootKey(lines, ModelKey, model, ref changed);
        UpsertRootKey(lines, ModelReasoningEffortKey, modelReasoningEffort, ref changed);

        if (!changed)
        {
            return original;
        }

        var result = string.Join(newline, lines);
        if (result.Length == 0)
        {
            return string.Empty;
        }

        if (hadTrailingNewline)
        {
            return result + newline;
        }

        return result;
    }

    private static void UpsertRootKey(List<string> lines, string key, string? value, ref bool changed)
    {
        var index = FindRootKeyLineIndex(lines, key, out var leadingWhitespace);

        if (value is null)
        {
            if (index >= 0)
            {
                lines.RemoveAt(index);
                changed = true;
            }

            return;
        }

        var desired = $"{leadingWhitespace}{key} = \"{EscapeTomlBasicString(value)}\"";
        if (index >= 0)
        {
            if (!string.Equals(lines[index], desired, StringComparison.Ordinal))
            {
                lines[index] = desired;
                changed = true;
            }

            return;
        }

        var insertAt = FindRootInsertIndex(lines);
        lines.Insert(insertAt, $"{key} = \"{EscapeTomlBasicString(value)}\"");
        changed = true;
    }

    private static int FindRootInsertIndex(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static int FindRootKeyLineIndex(IReadOnlyList<string> lines, string key, out string leadingWhitespace)
    {
        leadingWhitespace = string.Empty;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var afterKey = trimmed.AsSpan(key.Length).TrimStart();
            if (afterKey.Length == 0 || afterKey[0] != '=')
            {
                continue;
            }

            leadingWhitespace = line.Substring(0, line.Length - trimmed.Length);
            return i;
        }

        return -1;
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
