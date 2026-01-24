// CodexSessionStore：读取/创建 Codex CLI 的本地 sessions（默认路径：%USERPROFILE%\\.codex\\sessions）。
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace codex_bridge_server.Bridge;

public sealed class CodexSessionStore
{
    private readonly ILogger<CodexSessionStore> _logger;
    private readonly CodexCliInfo _cliInfo;
    private const string PlaceholderAssistantText = "（未输出正文）";
    private const int SettingsScanMaxBytes = 2 * 1024 * 1024;
    private const string TaskTitleGeneratorPromptPrefix =
        "You are a helpful assistant. You will be presented with a user prompt, and your job is to provide a short title for a task that will be created from that prompt.";

    public CodexSessionStore(ILogger<CodexSessionStore> logger, CodexCliInfo cliInfo)
    {
        _logger = logger;
        _cliInfo = cliInfo;
    }

    public IReadOnlyList<CodexSessionSummary> ListRecent(int limit)
    {
        limit = Math.Clamp(limit, 1, 200);

        var sessionsRoot = GetSessionsRoot();
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<CodexSessionSummary>();
        }

        var fileInfos = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    fileInfos.Add(new FileInfo(path));
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描 sessions 目录失败: {SessionsRoot}", sessionsRoot);
            return Array.Empty<CodexSessionSummary>();
        }

        fileInfos.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        var results = new List<CodexSessionSummary>(limit);
        foreach (var fi in fileInfos)
        {
            if (results.Count >= limit)
            {
                break;
            }

            var summary = TryReadSessionMeta(fi.FullName);
            if (summary is not null)
            {
                results.Add(summary);
            }
        }

        return results;
    }

    public CodexSessionSummary Create(string? cwd)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString();
        var resolvedCwd = NormalizeCwdOrThrow(cwd);
        var cliVersion = _cliInfo.GetCliVersion();

        var sessionsRoot = GetSessionsRoot();
        var year = now.ToString("yyyy");
        var month = now.ToString("MM");
        var day = now.ToString("dd");

        var dayDir = Path.Combine(sessionsRoot, year, month, day);
        Directory.CreateDirectory(dayDir);

        var fileTimestamp = now.ToString("yyyy-MM-dd'T'HH-mm-ss");
        var fileName = $"rollout-{fileTimestamp}-{sessionId}.jsonl";
        var filePath = Path.Combine(dayDir, fileName);

        var metaLine = new CodexSessionMetaLine
        {
            Timestamp = now,
            Type = "session_meta",
            Payload = new CodexSessionMetaPayload
            {
                Id = sessionId,
                Timestamp = now,
                Cwd = resolvedCwd,
                Originator = "codex_bridge",
                CliVersion = cliVersion,
                Instructions = string.Empty,
            },
        };

        var json = JsonSerializer.Serialize(metaLine, BridgeJson.SerializerOptions);
        using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine(json);
        }

        return new CodexSessionSummary
        {
            Id = sessionId,
            Title = BuildSessionTitle(firstUserMessageText: null, metaLine.Payload.Cwd, sessionId),
            CreatedAt = now,
            Cwd = metaLine.Payload.Cwd,
            Originator = metaLine.Payload.Originator,
            CliVersion = metaLine.Payload.CliVersion,
        };
    }

    public void EnsureSessionCwd(string sessionId, string? cwdCandidate)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var sessionsRoot = GetSessionsRoot();
        if (!Directory.Exists(sessionsRoot))
        {
            throw new InvalidOperationException($"未找到 Codex sessions 目录: {sessionsRoot}");
        }

        var sessionFilePath = TryFindSessionFilePath(sessionsRoot, sessionId);
        if (sessionFilePath is null)
        {
            throw new InvalidOperationException($"未找到会话文件: {sessionId}");
        }

        var hasUtf8Bom = HasUtf8Bom(sessionFilePath);

        List<string> lines;
        try
        {
            lines = new List<string>(capacity: 256);
            using var fs = new FileStream(sessionFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Add(line);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读取会话文件失败: {sessionId}", ex);
        }

        if (lines.Count == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            throw new InvalidOperationException($"会话文件为空或损坏: {sessionId}");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(lines[0]) as JsonObject
                ?? throw new InvalidOperationException("会话元数据不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("会话元数据不是有效 JSON", ex);
        }

        var needsRewrite = hasUtf8Bom;

        var type = root["type"]?.GetValue<string>();
        if (!string.Equals(type, "session_meta", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("会话文件首行不是 session_meta，无法自动修复 cwd");
        }

        if (root["payload"] is not JsonObject payload)
        {
            throw new InvalidOperationException("会话元数据缺少 payload，无法自动修复 cwd");
        }

        var existingCwd = payload["cwd"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(existingCwd))
        {
            // keep
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cwdCandidate))
            {
                throw new InvalidOperationException("该会话文件缺少 cwd，无法 resume。请在 Chat 页填写 workingDirectory，或重新创建会话。");
            }

            var resolvedCwd = NormalizeCwdOrThrow(cwdCandidate);
            payload["cwd"] = resolvedCwd;
            needsRewrite = true;
        }

        var existingCliVersion = payload["cli_version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(existingCliVersion))
        {
            payload["cli_version"] = _cliInfo.GetCliVersion();
            needsRewrite = true;
        }

        if (!needsRewrite)
        {
            return;
        }

        lines[0] = root.ToJsonString(BridgeJson.SerializerOptions);

        var tmpPath = sessionFilePath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }
            }

            File.Replace(tmpPath, sessionFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
            }
        }
    }

    public IReadOnlyList<CodexSessionMessage>? ReadMessages(string sessionId, int limit)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        limit = Math.Clamp(limit, 1, 2000);

        var sessionsRoot = GetSessionsRoot();
        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        var sessionFilePath = TryFindSessionFilePath(sessionsRoot, sessionId);
        if (sessionFilePath is null)
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(sessionFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var queue = new Queue<CodexSessionMessage>(Math.Min(limit, 128));
            var traceBuffer = new List<CodexSessionTraceEntry>(capacity: 16);
            var traceByCallId = new Dictionary<string, CodexSessionTraceEntry>(StringComparer.Ordinal);
            string? pendingAgentMessage = null;

            void FlushPendingAssistantMessage()
            {
                if (pendingAgentMessage is null && traceBuffer.Count == 0)
                {
                    return;
                }

                var text = string.IsNullOrWhiteSpace(pendingAgentMessage)
                    ? PlaceholderAssistantText
                    : pendingAgentMessage.Trim();

                if (queue.Count >= limit)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(new CodexSessionMessage
                {
                    Role = "assistant",
                    Text = text,
                    Trace = traceBuffer.Count > 0 ? traceBuffer.ToArray() : null,
                });

                pendingAgentMessage = null;
                traceBuffer.Clear();
                traceByCallId.Clear();
            }

            while (true)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (!TryParseMessageLine(line, out var role, out var text, out var kind, out var images))
                {
                    if (TryParseAgentReasoningLine(line, out var reasoningText))
                    {
                        var entry = CreateReasoningTraceEntry(reasoningText);
                        if (entry is not null)
                        {
                            traceBuffer.Add(entry);
                        }

                        continue;
                    }

                    if (TryParseAgentMessageLine(line, out var agentMessageText))
                    {
                        pendingAgentMessage = agentMessageText;
                        continue;
                    }

                    if (TryParseReasoningSummaryLine(line, out var reasoningSummaries))
                    {
                        foreach (var summary in reasoningSummaries)
                        {
                            var entry = CreateReasoningTraceEntry(summary);
                            if (entry is not null)
                            {
                                traceBuffer.Add(entry);
                            }
                        }

                        continue;
                    }

                    if (TryParseFunctionCallLine(line, out var callId, out var tool, out var command))
                    {
                        var entry = new CodexSessionTraceEntry
                        {
                            Kind = "command",
                            Tool = tool,
                            Command = command,
                            Status = "completed",
                        };
                        traceBuffer.Add(entry);

                        if (!string.IsNullOrWhiteSpace(callId))
                        {
                            traceByCallId[callId] = entry;
                        }

                        continue;
                    }

                    if (TryParseFunctionCallOutputLine(line, out var outputCallId, out var outputText, out var exitCode))
                    {
                        if (!string.IsNullOrWhiteSpace(outputCallId)
                            && traceByCallId.TryGetValue(outputCallId, out var entry))
                        {
                            entry.Output = string.IsNullOrWhiteSpace(outputText) ? null : outputText;
                            entry.ExitCode = exitCode;
                            entry.Status = "completed";
                        }

                        continue;
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                var isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
                var isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
                var hasImages = images is not null && images.Count > 0;
                var hasTrace = isAssistant && traceBuffer.Count > 0;
                var hasText = !string.IsNullOrWhiteSpace(text);

                if (!hasText && !hasImages && !hasTrace)
                {
                    continue;
                }

                if (isUser)
                {
                    FlushPendingAssistantMessage();

                    var sanitized = SanitizeUserMessageText(text);
                    if (string.IsNullOrWhiteSpace(sanitized) && (images is null || images.Count == 0))
                    {
                        continue;
                    }

                    text = sanitized ?? string.Empty;
                }
                else if (!isAssistant)
                {
                    continue;
                }

                if (isAssistant)
                {
                    if (string.IsNullOrWhiteSpace(text) && !hasImages && !string.IsNullOrWhiteSpace(pendingAgentMessage))
                    {
                        text = pendingAgentMessage;
                    }

                    pendingAgentMessage = null;

                    if (string.IsNullOrWhiteSpace(text) && hasTrace && !hasImages)
                    {
                        text = PlaceholderAssistantText;
                    }
                }

                if (queue.Count >= limit)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(new CodexSessionMessage
                {
                    Role = role,
                    Text = text,
                    Images = images,
                    Kind = kind,
                    Trace = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) && traceBuffer.Count > 0
                        ? traceBuffer.ToArray()
                        : null,
                });

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    traceBuffer.Clear();
                    traceByCallId.Clear();
                }
            }

            FlushPendingAssistantMessage();
            return queue.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话消息失败: {SessionId}", sessionId);
            return Array.Empty<CodexSessionMessage>();
        }
    }

    public CodexSessionSettingsSnapshot? TryReadLatestSettings(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sessionFilePath = TryGetSessionFilePath(sessionId);
        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
        {
            return null;
        }

        try
        {
            return ReadLatestSettingsFromSessionFile(sessionFilePath);
        }
        catch
        {
            return new CodexSessionSettingsSnapshot();
        }
    }

    public string? TryGetSessionFilePath(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            var sessionsRoot = GetSessionsRoot();
            if (!Directory.Exists(sessionsRoot))
            {
                return null;
            }

            return TryFindSessionFilePath(sessionsRoot, sessionId.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 删除会话文件，移至回收站。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>是否成功删除</returns>
    public bool Delete(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var filePath = TryGetSessionFilePath(sessionId);
        if (filePath is null || !File.Exists(filePath))
        {
            _logger.LogWarning("删除会话失败: 未找到会话文件 {SessionId}", sessionId);
            return false;
        }

        try
        {
            // 使用 Microsoft.VisualBasic 将文件移至回收站
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                filePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            _logger.LogInformation("已将会话文件移至回收站: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除会话文件失败: {FilePath}", filePath);
            return false;
        }
    }

    private static CodexSessionTraceEntry? CreateReasoningTraceEntry(string text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        SplitReasoningTitle(trimmed, out var title, out var detail);

        return new CodexSessionTraceEntry
        {
            Kind = "reasoning",
            Title = title,
            Text = detail,
        };
    }

    private static void SplitReasoningTitle(string text, out string? title, out string detail)
    {
        title = null;
        detail = text.Trim();

        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        if (detail.StartsWith("**", StringComparison.Ordinal))
        {
            var end = detail.IndexOf("**", startIndex: 2, StringComparison.Ordinal);
            if (end > 2)
            {
                title = detail.Substring(2, end - 2).Trim();
                var rest = detail.Substring(end + 2).Trim();
                detail = string.IsNullOrWhiteSpace(rest) ? detail : rest;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = null;
                }

                return;
            }
        }

        using var reader = new StringReader(detail);
        var firstLine = reader.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            title = firstLine.Length <= 80 ? firstLine : TruncateWithEllipsis(firstLine, 80);
        }
    }

    private static bool TryParseAgentReasoningLine(string line, out string text)
    {
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "event_msg", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "agent_reasoning", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(payload, "text", out var parsed) || string.IsNullOrWhiteSpace(parsed))
            {
                return false;
            }

            text = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseAgentMessageLine(string line, out string text)
    {
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "event_msg", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "agent_message", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(payload, "message", out var parsed) || string.IsNullOrWhiteSpace(parsed))
            {
                return false;
            }

            text = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseReasoningSummaryLine(string line, out IReadOnlyList<string> summaries)
    {
        summaries = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "response_item", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "reasoning", StringComparison.Ordinal))
            {
                return false;
            }

            if (!payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<string>(capacity: 4);
            foreach (var item in summary.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetString(item, "type", out var itemType) || !string.Equals(itemType, "summary_text", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryGetString(item, "text", out var itemText) || string.IsNullOrWhiteSpace(itemText))
                {
                    continue;
                }

                list.Add(itemText);
            }

            if (list.Count == 0)
            {
                return false;
            }

            summaries = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseFunctionCallLine(string line, out string? callId, out string tool, out string command)
    {
        callId = null;
        tool = string.Empty;
        command = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "response_item", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "function_call", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(payload, "name", out tool) || string.IsNullOrWhiteSpace(tool))
            {
                return false;
            }

            _ = TryGetString(payload, "call_id", out var parsedCallId);
            callId = string.IsNullOrWhiteSpace(parsedCallId) ? null : parsedCallId;

            if (TryGetString(payload, "arguments", out var args) && !string.IsNullOrWhiteSpace(args))
            {
                command = BuildCommandLabel(tool, args);
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                command = tool;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildCommandLabel(string tool, string arguments)
    {
        if (string.Equals(tool, "shell_command", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(arguments);
                var root = doc.RootElement;
                if (TryGetString(root, "command", out var cmd) && !string.IsNullOrWhiteSpace(cmd))
                {
                    return cmd;
                }
            }
            catch (JsonException)
            {
            }

            return "shell_command";
        }

        if (string.Equals(tool, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return "apply_patch";
        }

        return tool;
    }

    private static bool TryParseFunctionCallOutputLine(string line, out string? callId, out string? output, out int? exitCode)
    {
        callId = null;
        output = null;
        exitCode = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "response_item", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "function_call_output", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(payload, "call_id", out var parsedCallId) || string.IsNullOrWhiteSpace(parsedCallId))
            {
                return false;
            }

            callId = parsedCallId;

            _ = TryGetString(payload, "output", out var parsedOutput);
            output = string.IsNullOrWhiteSpace(parsedOutput) ? null : parsedOutput;

            if (!string.IsNullOrWhiteSpace(output))
            {
                exitCode = TryParseExitCode(output);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? TryParseExitCode(string output)
    {
        using var reader = new StringReader(output);
        var firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        const string prefix = "Exit code:";
        if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = firstLine.Substring(prefix.Length).Trim();
        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static CodexSessionSummary? TryReadSessionMeta(string filePath)
    {
        string? firstLine;
        string? firstUserMessageText = null;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            firstLine = reader.ReadLine();

            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                firstUserMessageText = TryReadFirstUserMessageText(reader);
            }
        }
        catch
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(firstUserMessageText) && ShouldHideSessionFromLists(firstUserMessageText))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetString(payload, "id", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            DateTimeOffset createdAt = DateTimeOffset.MinValue;
            if (TryGetString(payload, "timestamp", out var timestamp) && DateTimeOffset.TryParse(timestamp, out var parsed))
            {
                createdAt = parsed;
            }

            TryGetString(payload, "cwd", out var cwd);
            TryGetString(payload, "originator", out var originator);
            TryGetString(payload, "cli_version", out var cliVersion);

            return new CodexSessionSummary
            {
                Id = id,
                Title = BuildSessionTitle(firstUserMessageText, cwd, id),
                CreatedAt = createdAt == DateTimeOffset.MinValue
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(filePath))
                    : createdAt,
                Cwd = string.IsNullOrWhiteSpace(cwd) ? null : cwd,
                Originator = string.IsNullOrWhiteSpace(originator) ? null : originator,
                CliVersion = string.IsNullOrWhiteSpace(cliVersion) ? null : cliVersion,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement data, string propertyName, out string value)
    {
        value = string.Empty;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (text is null)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static string GetSessionsRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CODEX_RELAYOUTER_SESSIONS_ROOT")
            ?? Environment.GetEnvironmentVariable("CODEX_SESSIONS_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return overrideRoot.Trim();
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex", "sessions");
    }

    private static CodexSessionSettingsSnapshot ReadLatestSettingsFromSessionFile(string sessionFilePath)
    {
        var tail = ReadTailText(sessionFilePath, maxBytes: SettingsScanMaxBytes);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return new CodexSessionSettingsSnapshot();
        }

        var lines = tail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string? approvalPolicy = null;
        string? sandbox = null;

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (approvalPolicy is not null && sandbox is not null)
            {
                break;
            }

            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.Contains("approval", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                ExtractSettingsFromJson(doc.RootElement, ref approvalPolicy, ref sandbox);
            }
            catch (JsonException)
            {
            }
        }

        return new CodexSessionSettingsSnapshot
        {
            ApprovalPolicy = approvalPolicy,
            Sandbox = sandbox,
        };
    }

    private static void ExtractSettingsFromJson(JsonElement element, ref string? approvalPolicy, ref string? sandbox)
    {
        if (approvalPolicy is null)
        {
            if (TryGetStringRecursive(element, "approval_policy", out var parsed)
                || TryGetStringRecursive(element, "approvalPolicy", out parsed))
            {
                approvalPolicy = NormalizeSettingValue(parsed);
            }
        }

        if (sandbox is null)
        {
            if (TryGetStringRecursive(element, "sandbox_mode", out var parsed)
                || TryGetStringRecursive(element, "sandboxMode", out parsed)
                || TryGetStringRecursive(element, "sandbox", out parsed))
            {
                sandbox = NormalizeSettingValue(parsed);
            }
        }
    }

    private static string? NormalizeSettingValue(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool TryGetStringRecursive(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var candidate = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        value = candidate;
                        return true;
                    }
                }

                if (TryGetStringRecursive(property.Value, propertyName, out value))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryGetStringRecursive(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ReadTailText(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var offset = Math.Max(0, length - maxBytes);
        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[3];
            var read = fs.Read(buf);
            return read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeCwdOrThrow(string? cwd)
    {
        var trimmed = cwd?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("cwd 不能为空");
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"cwd 路径无效: {trimmed}", ex);
        }

        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"工作区目录不存在或不可访问: {full}");
        }

        return full;
    }

    private string? TryFindSessionFilePath(string sessionsRoot, string sessionId)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(path).Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return path;
            }

            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var firstLine = reader.ReadLine();
                    if (firstLine is null)
                    {
                        continue;
                    }

                    if (TryExtractSessionIdFromMetaLine(firstLine, out var extractedSessionId)
                        && string.Equals(extractedSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查找会话文件失败: {SessionId}", sessionId);
        }

        return null;
    }

    private static bool TryExtractSessionIdFromMetaLine(string line, out string sessionId)
    {
        sessionId = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || !string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "id", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            sessionId = id;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryReadFirstUserMessageText(StreamReader reader)
    {
        var maxLines = 500;

        for (var i = 0; i < maxLines; i++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                return null;
            }

            if (!TryParseMessageLine(line, out var role, out var text, out _, out _))
            {
                continue;
            }

            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var sanitized = SanitizeUserMessageText(text);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                continue;
            }

            return sanitized;
        }

        return null;
    }

    private static string BuildSessionTitle(string? firstUserMessageText, string? cwd, string id)
    {
        if (!string.IsNullOrWhiteSpace(firstUserMessageText))
        {
            var sanitized = SanitizeUserMessageText(firstUserMessageText);
            var normalized = NormalizeSingleLine(sanitized ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return TruncateWithEllipsis(normalized, 50);
            }
        }

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            return cwd.Trim();
        }

        return id;
    }

    private static string NormalizeSingleLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasWhitespace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    sb.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            sb.Append(ch);
            lastWasWhitespace = false;
        }

        return sb.ToString().Trim();
    }

    private static string TruncateWithEllipsis(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars == 1)
        {
            return "…";
        }

        return string.Concat(text.AsSpan(0, maxChars - 1), "…");
    }

    private static string? SanitizeUserMessageText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var extracted = TryExtractMyRequestForCodex(text);
        var candidate = (extracted ?? text).Trim();

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (LooksLikeHarnessBoilerplate(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static string? TryExtractMyRequestForCodex(string text)
    {
        using var reader = new StringReader(text);

        var sb = new StringBuilder();
        var found = false;

        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (!found)
            {
                if (IsMyRequestForCodexHeaderLine(line))
                {
                    found = true;
                }

                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(line);
        }

        if (!found)
        {
            return null;
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool IsMyRequestForCodexHeaderLine(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("##", StringComparison.Ordinal))
        {
            return false;
        }

        var title = trimmed.TrimStart('#').Trim();
        if (!title.StartsWith("My request for Codex", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeHarnessBoilerplate(string text)
    {
        var normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("agents.md instructions for", StringComparison.Ordinal)
            || lower.Contains("<instructions>", StringComparison.Ordinal)
            || lower.Contains("</instructions>", StringComparison.Ordinal)
            || lower.Contains("<environment_context>", StringComparison.Ordinal)
            || lower.Contains("</environment_context>", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("# context from my ide setup", StringComparison.Ordinal)
            || lower.Contains("## active file:", StringComparison.Ordinal)
            || lower.Contains("## open tabs:", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldHideSessionFromLists(string firstUserMessageText)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessageText))
        {
            return false;
        }

        var trimmed = firstUserMessageText.TrimStart();
        return trimmed.StartsWith(TaskTitleGeneratorPromptPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseMessageLine(
        string line,
        out string role,
        out string text,
        out string? kind,
        out IReadOnlyList<string>? images)
    {
        role = string.Empty;
        text = string.Empty;
        kind = null;
        images = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var lineType) || !string.Equals(lineType, "response_item", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "message", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(payload, "role", out var parsedRole) || string.IsNullOrWhiteSpace(parsedRole))
            {
                return false;
            }

            if (!payload.TryGetProperty("content", out var content))
            {
                return false;
            }

            ExtractMessageTextAndImages(content, out var extractedText, out var extractedImages);
            if (string.IsNullOrWhiteSpace(extractedText) && (extractedImages is null || extractedImages.Count == 0))
            {
                return false;
            }

            role = parsedRole;
            text = extractedText;
            images = extractedImages;
            kind = payloadType;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ExtractMessageTextAndImages(
        JsonElement content,
        out string text,
        out IReadOnlyList<string>? images)
    {
        text = string.Empty;
        images = null;

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
            return;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(content, "text", out var single))
            {
                text = single;
            }

            if (TryGetDataUrlFromImageObject(content, out var dataUrl))
            {
                images = new[] { dataUrl };
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var sb = new StringBuilder();
        List<string>? imageList = null;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetString(item, "text", out var partText) || string.IsNullOrEmpty(partText))
            {
                if (!TryGetDataUrlFromImageObject(item, out var dataUrl))
                {
                    continue;
                }

                imageList ??= new List<string>(capacity: 1);
                imageList.Add(dataUrl);
                continue;
            }

            var normalized = partText.Trim();
            if (string.Equals(normalized, "<image>", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "</image>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(partText);
        }

        text = sb.ToString();
        images = imageList is null || imageList.Count == 0 ? null : imageList.ToArray();
    }

    private static bool TryGetDataUrlFromImageObject(JsonElement item, out string dataUrl)
    {
        dataUrl = string.Empty;

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? candidate = null;
        if (item.TryGetProperty("image_url", out var imageUrlProp))
        {
            if (imageUrlProp.ValueKind == JsonValueKind.String)
            {
                candidate = imageUrlProp.GetString();
            }
            else if (imageUrlProp.ValueKind == JsonValueKind.Object && TryGetString(imageUrlProp, "url", out var url))
            {
                candidate = url;
            }
        }
        else if (TryGetString(item, "url", out var directUrl))
        {
            candidate = directUrl;
        }

        candidate = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        dataUrl = candidate;
        return true;
    }

    private sealed class CodexSessionMetaLine
    {
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("payload")]
        public required CodexSessionMetaPayload Payload { get; init; }
    }

    private sealed class CodexSessionMetaPayload
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; init; }

        [JsonPropertyName("cwd")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Cwd { get; init; }

        [JsonPropertyName("originator")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Originator { get; init; }

        [JsonPropertyName("cli_version")]
        public required string CliVersion { get; init; }

        [JsonPropertyName("instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Instructions { get; init; }
    }
}
