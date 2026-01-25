using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class CodexAppServerRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IOptions<CodexOptions> _options;
    private readonly ILogger<CodexAppServerRunner> _logger;
    private readonly CodexTurnPlanStore _turnPlanStore;

    public CodexAppServerRunner(
        IOptions<CodexOptions> options,
        ILogger<CodexAppServerRunner> logger,
        CodexTurnPlanStore turnPlanStore)
    {
        _options = options;
        _logger = logger;
        _turnPlanStore = turnPlanStore;
    }

    public async Task<CodexAppServerTurnResult> RunTurnAsync(
        string runId,
        CodexRunRequest request,
        Func<BridgeEnvelope, CancellationToken, Task> emitEvent,
        Func<CodexAppServerApprovalRequest, CancellationToken, Task<CodexAppServerApprovalDecision>> requestApproval,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;

        var invocation = CodexRunner.ResolveCodexInvocation(options.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true,
        };

        foreach (var arg in invocation.PrefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add("app-server");

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            var workingDirectory = request.WorkingDirectory.Trim();
            if (!Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException($"工作区目录不存在或不可访问: {workingDirectory}");
            }

            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>();
        var nextId = 0L;

        var currentThreadId = string.Empty;
        var currentTurnId = string.Empty;
        var diffTracker = new DiffRunTracker();
        var diffSummaryEmitted = false;
        var reasoningSummaryTracker = new ReasoningSummaryTracker();

        var completedTcs = new TaskCompletionSource<CodexAppServerTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SendAsync(object message, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(message, BridgeJson.SerializerOptions);
            await process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }

        Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            var message = new { id, method, @params };

            _ = SendAsync(message, ct).ContinueWith(task =>
            {
                if (task.IsFaulted && task.Exception is not null)
                {
                    tcs.TrySetException(task.Exception);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return tcs.Task;
        }

        async Task InitializeAsync(CancellationToken ct)
        {
            _ = await SendRequestAsync(
                "initialize",
                new { clientInfo = new { name = "codex-relayouter-server", version = "0.0" } },
                ct);
        }

        async Task ReloadMcpServerConfigBestEffortAsync(CancellationToken ct)
        {
            try
            {
                _ = await SendRequestAsync("config/mcpServer/reload", @params: new { }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "MCP server 配置重载失败（将继续运行，不影响非 MCP 任务）");
            }
        }

        async Task WarmupMcpServersBestEffortAsync(CancellationToken ct)
        {
            try
            {
                // 某些 MCP server（例如 npx 启动）需要一点时间完成拉起并返回 tools 列表。
                // 为避免首个 turn 开始时模型“看不到”工具，这里做一次轻量预热（有超时上限，失败不阻断）。
                var startedAt = DateTimeOffset.UtcNow;
                var attempt = 0;
                var delayMs = 150;

                while (!ct.IsCancellationRequested)
                {
                    attempt++;

                    var status = await SendRequestAsync(
                        "mcpServerStatus/list",
                        new { cursor = (string?)null, limit = 100 },
                        ct);

                    var (serverCount, toolCount) = CountMcpServersAndTools(status);
                    if (toolCount > 0)
                    {
                        _logger.LogInformation("MCP 已就绪: servers={ServerCount} tools={ToolCount} attempts={Attempts}", serverCount, toolCount, attempt);
                        return;
                    }

                    // 无配置时直接返回；避免每次 turn 额外等待。
                    if (serverCount == 0)
                    {
                        return;
                    }

                    var elapsed = DateTimeOffset.UtcNow - startedAt;
                    if (elapsed > TimeSpan.FromSeconds(6))
                    {
                        _logger.LogInformation("MCP 预热超时: servers={ServerCount} tools={ToolCount} attempts={Attempts}", serverCount, toolCount, attempt);
                        return;
                    }

                    await Task.Delay(delayMs, ct);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "MCP 预热失败（将继续运行，不影响非 MCP 任务）");
            }
        }

        static (int serverCount, int toolCount) CountMcpServersAndTools(JsonElement status)
        {
            var serverCount = 0;
            var toolCount = 0;

            if (status.ValueKind != JsonValueKind.Object)
            {
                return (serverCount, toolCount);
            }

            if (!status.TryGetProperty("data", out var data))
            {
                return (serverCount, toolCount);
            }

            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in data.EnumerateArray())
                {
                    if (server.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    serverCount++;
                    if (!server.TryGetProperty("tools", out var tools))
                    {
                        continue;
                    }

                    toolCount += tools.ValueKind switch
                    {
                        JsonValueKind.Object => tools.EnumerateObject().Count(),
                        JsonValueKind.Array => tools.GetArrayLength(),
                        _ => 0,
                    };
                }

                return (serverCount, toolCount);
            }

            // 兼容可能返回对象结构的实现：data.servers[].tools
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    serverCount++;
                    if (!server.TryGetProperty("tools", out var tools))
                    {
                        continue;
                    }

                    toolCount += tools.ValueKind switch
                    {
                        JsonValueKind.Object => tools.EnumerateObject().Count(),
                        JsonValueKind.Array => tools.GetArrayLength(),
                        _ => 0,
                    };
                }
            }

            return (serverCount, toolCount);
        }

        async Task<string> EnsureThreadAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                var resumeResult = await SendRequestAsync(
                    "thread/resume",
                    new { threadId = request.SessionId.Trim() },
                    ct);

                if (TryGetThreadIdFromThreadResponse(resumeResult, out var resumedThreadId))
                {
                    return resumedThreadId;
                }

                throw new InvalidOperationException("thread/resume 未返回有效 thread.id");
            }

            var startResult = await SendRequestAsync(
                "thread/start",
                new
                {
                    cwd = request.WorkingDirectory,
                    model = request.Model,
                    sandbox = string.IsNullOrWhiteSpace(request.Sandbox) ? null : request.Sandbox,
                    approvalPolicy = string.IsNullOrWhiteSpace(request.ApprovalPolicy) ? null : request.ApprovalPolicy,
                },
                ct);

            if (!TryGetThreadIdFromThreadResponse(startResult, out var threadId))
            {
                throw new InvalidOperationException("thread/start 未返回有效 thread.id");
            }

            await emitEvent(
                CreateEvent("session.created", new { runId, sessionId = threadId }),
                ct);

            return threadId;
        }

        async Task<(string turnId, JsonElement turn)> StartTurnAsync(string threadId, CancellationToken ct)
        {
            var input = BuildTurnInput(request.Prompt, request.Images);

            var turnStartResult = await SendRequestAsync(
                "turn/start",
                new
                {
                    threadId,
                    input,
                    cwd = request.WorkingDirectory,
                    model = request.Model,
                    effort = string.IsNullOrWhiteSpace(request.Effort) ? null : request.Effort,
                    approvalPolicy = string.IsNullOrWhiteSpace(request.ApprovalPolicy) ? null : request.ApprovalPolicy,
                    sandboxPolicy = CreateSandboxPolicy(request.Sandbox, request.WorkingDirectory),
                },
                ct);

            if (turnStartResult.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("turn/start 未返回对象");
            }

            if (!turnStartResult.TryGetProperty("turn", out var turn) || turn.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("turn/start 未返回 turn");
            }

            if (!turn.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("turn/start 未返回 turn.id");
            }

            var turnId = id.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(turnId))
            {
                throw new InvalidOperationException("turn/start 返回的 turn.id 为空");
            }

            return (turnId, turn);
        }

        async Task HandleStdErrAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    _logger.LogWarning("codex(app-server) stderr: {Line}", line);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 codex(app-server) stderr 失败");
            }
        }

        async Task HandleStdOutAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!TryParseJson(line, out var root))
                    {
                        continue;
                    }

                    if (TryHandleResponse(root))
                    {
                        continue;
                    }

                    if (TryHandleServerRequest(root, ct))
                    {
                        continue;
                    }

                    await HandleNotificationAsync(root, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 codex(app-server) stdout 失败");
            }
        }

        bool TryHandleResponse(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // JSON-RPC request/notification messages include "method". app-server 会主动发起
            // item/*/requestApproval 等请求；不能把这些消息当作 response 吞掉。
            if (TryGetString(root, "method", out var method) && !string.IsNullOrWhiteSpace(method))
            {
                return false;
            }

            if (!TryGetInt64(root, "id", out var id))
            {
                return false;
            }

            if (!pending.TryRemove(id, out var tcs))
            {
                return true;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                    ? msgProp.GetString()
                    : null;

                tcs.TrySetException(new InvalidOperationException(message ?? "codex app-server 请求失败"));
                return true;
            }

            if (!root.TryGetProperty("result", out var result))
            {
                tcs.TrySetException(new InvalidOperationException("codex app-server 响应缺少 result"));
                return true;
            }

            tcs.TrySetResult(result);
            return true;
        }

        bool TryHandleServerRequest(JsonElement root, CancellationToken ct)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetInt64(root, "id", out var id))
            {
                return false;
            }

            if (!TryGetString(root, "method", out var method) || string.IsNullOrWhiteSpace(method))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (string.Equals(method, "item/commandExecution/requestApproval", StringComparison.Ordinal))
                    {
                        var approval = ParseApprovalRequest(id, kind: "commandExecution", @params);
                        var decision = await requestApproval(approval, ct);
                        await SendAsync(new { id, result = BuildCommandExecutionApprovalResult(decision) }, ct);
                        return;
                    }

                    if (string.Equals(method, "item/fileChange/requestApproval", StringComparison.Ordinal))
                    {
                        var approval = ParseApprovalRequest(id, kind: "fileChange", @params);
                        var hasPayload = TryGetFileChangePayloadsFromApprovalParams(@params, out var payloads);
                        var decision = await requestApproval(approval, ct);
                        var normalizedDecision = NormalizeDecision(decision.Decision, defaultValue: "decline");

                        if (hasPayload && IsAcceptedDecision(normalizedDecision))
                        {
                            var updates = new List<object>(capacity: payloads.Count);
                            foreach (var payload in payloads)
                            {
                                var snapshot = diffTracker.Update(payload.Path, payload.Diff, payload.Added, payload.Removed);
                                if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Diff))
                                {
                                    continue;
                                }

                                updates.Add(
                                    new
                                    {
                                        path = snapshot.Path,
                                        diff = snapshot.Diff,
                                        added = snapshot.Added,
                                        removed = snapshot.Removed,
                                    });
                            }

                            if (updates.Count > 0)
                            {
                                await emitEvent(
                                    CreateEvent(
                                        "diff.updated",
                                        new
                                        {
                                            runId,
                                            threadId = currentThreadId,
                                            files = updates,
                                        }),
                                    ct);
                            }
                        }

                        await SendAsync(new { id, result = new { decision = normalizedDecision } }, ct);
                        return;
                    }

                    await SendAsync(new { id, error = new { code = -32601, message = $"不支持的请求: {method}" } }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "处理 codex(app-server) server request 失败: {Method}", method);
                    try
                    {
                        await SendAsync(new { id, error = new { code = -32000, message = ex.Message } }, ct);
                    }
                    catch
                    {
                    }
                }
            }, CancellationToken.None);

            return true;
        }

        async Task EmitDiffSummaryAsync(CancellationToken ct)
        {
            if (diffSummaryEmitted || !diffTracker.HasChanges)
            {
                return;
            }

            diffSummaryEmitted = true;
            var summary = diffTracker.BuildSummary();
            await emitEvent(
                CreateEvent(
                    "diff.summary",
                    new
                    {
                        runId,
                        threadId = currentThreadId,
                        files = summary.Files,
                        totalAdded = summary.TotalAdded,
                        totalRemoved = summary.TotalRemoved,
                    }),
                ct);
        }

        async Task HandleNotificationAsync(JsonElement root, CancellationToken ct)
        {
            if (!TryGetString(root, "method", out var method) || string.IsNullOrWhiteSpace(method))
            {
                return;
            }

            if (!root.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (@params.TryGetProperty("threadId", out var threadIdProp) && threadIdProp.ValueKind == JsonValueKind.String)
            {
                var tid = threadIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(tid) && string.IsNullOrWhiteSpace(currentThreadId))
                {
                    currentThreadId = tid;
                }

                if (!string.IsNullOrWhiteSpace(currentThreadId) && !string.Equals(tid, currentThreadId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (@params.TryGetProperty("turnId", out var turnIdProp) && turnIdProp.ValueKind == JsonValueKind.String)
            {
                var tid = turnIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(currentTurnId) && !string.Equals(tid, currentTurnId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            switch (method)
            {
                case "turn/completed":
                    {
                        if (!@params.TryGetProperty("turn", out var turnCompleted) || turnCompleted.ValueKind != JsonValueKind.Object)
                        {
                            return;
                        }

                        if (!TryGetString(turnCompleted, "id", out var completedTurnId) || string.IsNullOrWhiteSpace(completedTurnId))
                        {
                            return;
                        }

                        if (!string.Equals(completedTurnId, currentTurnId, StringComparison.Ordinal))
                        {
                            return;
                        }

                        var status = TryGetString(turnCompleted, "status", out var turnStatus) ? turnStatus : null;
                        completedTcs.TrySetResult(
                            new CodexAppServerTurnResult
                            {
                                ThreadId = currentThreadId,
                                TurnId = currentTurnId,
                                Status = status ?? "completed",
                                Turn = turnCompleted,
                            });
                        await EmitDiffSummaryAsync(ct);
                        return;
                    }
                case "turn/plan/updated":
                    {
                        var threadId = currentThreadId;
                        if (TryGetString(@params, "threadId", out var threadIdText) && !string.IsNullOrWhiteSpace(threadIdText))
                        {
                            threadId = threadIdText.Trim();
                        }

                        var turnId = currentTurnId;
                        if (TryGetString(@params, "turnId", out var turnIdText) && !string.IsNullOrWhiteSpace(turnIdText))
                        {
                            turnId = turnIdText.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
                        {
                            return;
                        }

                        if (!@params.TryGetProperty("plan", out var planProp) || planProp.ValueKind != JsonValueKind.Array)
                        {
                            return;
                        }

                        string? explanation = null;
                        if (@params.TryGetProperty("explanation", out var explanationProp) && explanationProp.ValueKind == JsonValueKind.String)
                        {
                            explanation = explanationProp.GetString();
                        }

                        var steps = new List<TurnPlanStep>(capacity: 8);
                        foreach (var entry in planProp.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!TryGetString(entry, "step", out var step) || string.IsNullOrWhiteSpace(step))
                            {
                                continue;
                            }

                            if (!TryGetString(entry, "status", out var status) || string.IsNullOrWhiteSpace(status))
                            {
                                continue;
                            }

                            steps.Add(new TurnPlanStep(step.Trim(), status.Trim()));
                        }

                        var snapshot = new TurnPlanSnapshot(
                            SessionId: threadId,
                            TurnId: turnId,
                            Explanation: string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim(),
                            Plan: steps.ToArray(),
                            UpdatedAt: DateTimeOffset.UtcNow);

                        _turnPlanStore.Upsert(snapshot);

                        await emitEvent(
                            CreateEvent(
                                "run.plan.updated",
                                new
                                {
                                    runId,
                                    threadId,
                                    turnId,
                                    explanation = snapshot.Explanation,
                                    plan = snapshot.Plan,
                                    updatedAt = snapshot.UpdatedAt,
                                }),
                            ct);
                        return;
                    }
                case "item/agentMessage/delta":
                    {
                        if (!TryGetString(@params, "delta", out var delta) || string.IsNullOrEmpty(delta))
                        {
                            return;
                        }

                        if (!TryGetString(@params, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                        {
                            return;
                        }

                        await emitEvent(CreateEvent("chat.message.delta", new { runId, itemId, delta }), ct);
                        return;
                    }
                case "item/reasoning/summaryTextDelta":
                    {
                        if (!TryGetString(@params, "delta", out var delta) || string.IsNullOrEmpty(delta))
                        {
                            return;
                        }

                        if (!TryGetString(@params, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                        {
                            return;
                        }

                        itemId = itemId.Trim();
                        var summaryIndex = TryGetInt64(@params, "summaryIndex", out var idx) ? idx : 0;
                        if (reasoningSummaryTracker.TryAppendDelta(itemId, summaryIndex, delta, out var completedPart))
                        {
                            await emitEvent(CreateEvent("run.reasoning", new { runId, itemId = completedPart.PartId, text = completedPart.Text }), ct);
                        }

                        var partId = $"{itemId}_summary_{summaryIndex}";
                        await emitEvent(CreateEvent("run.reasoning.delta", new { runId, itemId = partId, textDelta = delta }), ct);
                        return;
                    }
                case "item/commandExecution/outputDelta":
                    {
                        if (!TryGetString(@params, "delta", out var delta) || string.IsNullOrEmpty(delta))
                        {
                            return;
                        }

                        if (!TryGetString(@params, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                        {
                            return;
                        }

                        await emitEvent(CreateEvent("run.command.outputDelta", new { runId, itemId, delta }), ct);
                        return;
                    }
                case "item/started":
                    {
                        if (!@params.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                        {
                            return;
                        }

                        if (!TryGetString(item, "type", out var itemType) || string.IsNullOrWhiteSpace(itemType))
                        {
                            return;
                        }

                        if (!TryGetString(item, "id", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                        {
                            return;
                        }

                        if (string.Equals(itemType, "commandExecution", StringComparison.Ordinal))
                        {
                            if (!TryGetString(item, "command", out var command) || string.IsNullOrWhiteSpace(command))
                            {
                                return;
                            }

                            var status = TryGetString(item, "status", out var statusText) ? statusText : "inProgress";
                            await emitEvent(CreateEvent("run.command", new { runId, itemId, command, status }), ct);
                        }

                        return;
                    }
                case "item/completed":
                    {
                        if (!@params.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                        {
                            return;
                        }

                        if (!TryGetString(item, "type", out var itemType) || string.IsNullOrWhiteSpace(itemType))
                        {
                            return;
                        }

                        if (!TryGetString(item, "id", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                        {
                            return;
                        }

                        if (string.Equals(itemType, "agentMessage", StringComparison.Ordinal))
                        {
                            if (!TryGetString(item, "text", out var text) || string.IsNullOrWhiteSpace(text))
                            {
                                return;
                            }

                            await emitEvent(CreateEvent("chat.message", new { runId, role = "assistant", text }), ct);
                            return;
                        }

                        if (IsFileChangeItemType(itemType))
                        {
                            if (!TryGetFileChangePayloads(item, out var payloads))
                            {
                                return;
                            }

                            var updates = new List<object>(capacity: payloads.Count);
                            foreach (var payload in payloads)
                            {
                                var snapshot = diffTracker.Update(payload.Path, payload.Diff, payload.Added, payload.Removed);
                                if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Diff))
                                {
                                    continue;
                                }

                                updates.Add(
                                    new
                                    {
                                        path = snapshot.Path,
                                        diff = snapshot.Diff,
                                        added = snapshot.Added,
                                        removed = snapshot.Removed,
                                    });
                            }

                            if (updates.Count == 0)
                            {
                                return;
                            }

                            await emitEvent(
                                CreateEvent(
                                    "diff.updated",
                                    new
                                    {
                                        runId,
                                        threadId = currentThreadId,
                                        files = updates,
                                    }),
                                ct);
                            return;
                        }

                        if (string.Equals(itemType, "commandExecution", StringComparison.Ordinal))
                        {
                            if (!TryGetString(item, "command", out var command) || string.IsNullOrWhiteSpace(command))
                            {
                                return;
                            }

                            var status = TryGetString(item, "status", out var statusText) ? statusText : "completed";
                            var output = TryGetString(item, "aggregatedOutput", out var outputText) ? outputText : null;
                            var hasExitCode = TryGetInt32(item, "exitCode", out var exitCode);

                            await emitEvent(
                                CreateEvent(
                                    "run.command",
                                    new
                                    {
                                        runId,
                                        itemId,
                                        command,
                                        status,
                                        exitCode = hasExitCode ? exitCode : (int?)null,
                                        output,
                                    }),
                                ct);
                            return;
                        }

                        if (string.Equals(itemType, "reasoning", StringComparison.Ordinal))
                        {
                            if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                            {
                                var index = 0L;
                                foreach (var part in summary.EnumerateArray())
                                {
                                    if (part.ValueKind != JsonValueKind.String)
                                    {
                                        continue;
                                    }

                                    var text = part.GetString();
                                    if (string.IsNullOrWhiteSpace(text))
                                    {
                                        continue;
                                    }

                                    var partId = $"{itemId}_summary_{index}";
                                    await emitEvent(CreateEvent("run.reasoning", new { runId, itemId = partId, text }), ct);
                                    index++;
                                }

                                reasoningSummaryTracker.Clear(itemId);
                                return;
                            }

                            var flushedParts = reasoningSummaryTracker.FinalizeFromSummary(itemId, summaryParts: null);
                            foreach (var part in flushedParts)
                            {
                                await emitEvent(CreateEvent("run.reasoning", new { runId, itemId = part.PartId, text = part.Text }), ct);
                            }

                            return;
                        }

                        return;
                    }
            }
        }

        var stderrTask = HandleStdErrAsync(cancellationToken);
        var stdoutTask = HandleStdOutAsync(cancellationToken);

        await InitializeAsync(cancellationToken);

        currentThreadId = await EnsureThreadAsync(cancellationToken);
        await ReloadMcpServerConfigBestEffortAsync(cancellationToken);
        await WarmupMcpServersBestEffortAsync(cancellationToken);

        var (turnId, initialTurn) = await StartTurnAsync(currentThreadId, cancellationToken);
        currentTurnId = turnId;

        await emitEvent(CreateEvent("turn.started", new { runId, threadId = currentThreadId, turnId = currentTurnId }), cancellationToken);

        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(completedTcs.Task, exitTask, cancelTask);

            if (completed == cancelTask)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (completed == exitTask)
            {
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"codex app-server 异常退出: exitCode={process.ExitCode}");
                }

                await EmitDiffSummaryAsync(cancellationToken);
                return new CodexAppServerTurnResult
                {
                    ThreadId = currentThreadId,
                    TurnId = currentTurnId,
                    Status = "completed",
                    Turn = initialTurn,
                };
            }

            var completedTurn = await completedTcs.Task;
            await EmitDiffSummaryAsync(cancellationToken);
            return completedTurn;
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch
            {
            }
        }
    }

    private static bool TryGetThreadIdFromThreadResponse(JsonElement result, out string threadId)
    {
        threadId = string.Empty;

        if (result.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!thread.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        threadId = id.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(threadId);
    }

    private static object? CreateSandboxPolicy(string? sandboxMode, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sandboxMode))
        {
            return null;
        }

        var mode = sandboxMode.Trim();

        return mode switch
        {
            "read-only" => new { type = "readOnly" },
            "workspace-write" => new
            {
                type = "workspaceWrite",
                writableRoots = string.IsNullOrWhiteSpace(workingDirectory) ? Array.Empty<string>() : new[] { workingDirectory },
                networkAccess = true,
                excludeTmpdirEnvVar = false,
                excludeSlashTmp = false,
            },
            "danger-full-access" => new { type = "dangerFullAccess" },
            _ => null,
        };
    }

    private static object[] BuildTurnInput(string prompt, IReadOnlyList<string>? imageDataUrls)
    {
        var items = new List<object>(capacity: 1 + (imageDataUrls?.Count ?? 0));

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            items.Add(new { type = "text", text = prompt });
        }

        if (imageDataUrls is not null)
        {
            foreach (var entry in imageDataUrls)
            {
                var url = entry?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                items.Add(new { type = "image", url });
            }
        }

        if (items.Count == 0)
        {
            items.Add(new { type = "text", text = string.Empty });
        }

        return items.ToArray();
    }

    private static bool TryParseJson(string line, out JsonElement root)
    {
        root = default;

        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CodexAppServerApprovalRequest ParseApprovalRequest(long requestId, string kind, JsonElement @params)
    {
        TryGetString(@params, "threadId", out var threadId);
        TryGetString(@params, "turnId", out var turnId);
        TryGetString(@params, "itemId", out var itemId);
        TryGetString(@params, "reason", out var reason);
        TryGetString(@params, "grantRoot", out var grantRoot);

        string[]? proposed = null;
        if (@params.TryGetProperty("proposedExecpolicyAmendment", out var proposedProp) && proposedProp.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var entry in proposedProp.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = entry.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }

            proposed = list.Count == 0 ? null : list.ToArray();
        }

        return new CodexAppServerApprovalRequest
        {
            RequestId = requestId.ToString(),
            Kind = kind,
            ThreadId = threadId ?? string.Empty,
            TurnId = turnId ?? string.Empty,
            ItemId = itemId,
            Reason = reason,
            ProposedExecpolicyAmendment = proposed,
            GrantRoot = grantRoot,
        };
    }

    private static object BuildCommandExecutionApprovalResult(CodexAppServerApprovalDecision decision)
    {
        var normalized = NormalizeDecision(decision.Decision, defaultValue: "decline");

        if (string.Equals(normalized, "acceptWithExecpolicyAmendment", StringComparison.Ordinal)
            && decision.ExecpolicyAmendment is { Length: > 0 })
        {
            return new
            {
                decision = new
                {
                    acceptWithExecpolicyAmendment = new
                    {
                        execpolicy_amendment = decision.ExecpolicyAmendment,
                    },
                },
            };
        }

        return new { decision = normalized };
    }

    private static string NormalizeDecision(string? decision, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(decision))
        {
            return defaultValue;
        }

        var trimmed = decision.Trim();
        return trimmed switch
        {
            "accept" => "accept",
            "acceptForSession" => "acceptForSession",
            "acceptWithExecpolicyAmendment" => "acceptWithExecpolicyAmendment",
            "decline" => "decline",
            "cancel" => "cancel",
            _ => defaultValue,
        };
    }

    private static bool TryGetString(JsonElement data, string propertyName, out string? value)
    {
        value = null;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryGetInt64(JsonElement data, string propertyName, out long value)
    {
        value = 0;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetInt64(out value);
    }

    private static bool TryGetInt32(JsonElement data, string propertyName, out int value)
    {
        value = 0;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetInt32(out value);
    }

    private static bool TryGetStringAny(JsonElement data, out string? value, params string[] propertyNames)
    {
        value = null;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in propertyNames)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                continue;
            }

            if (!data.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            value = property.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetInt32Any(JsonElement data, out int value, params string[] propertyNames)
    {
        value = 0;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in propertyNames)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                continue;
            }

            if (!data.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (property.TryGetInt32(out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAcceptedDecision(string decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
        {
            return false;
        }

        var normalized = decision.Trim();
        return normalized.StartsWith("accept", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileChangeItemType(string itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return false;
        }

        var normalized = itemType.Trim();
        if (string.Equals(normalized, "fileChange", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(normalized, "file_change", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(normalized, "filechange", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct FileChangePayload(string Path, string? Diff, int? Added, int? Removed);

    private static bool TryGetFileChangePayloads(JsonElement item, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        if (TryCollectFileChangePayloadsFromContainer(item, out var collected))
        {
            payloads = collected;
            return true;
        }

        if (TryCollectFileChangePayloadFromSingle(item, out var single))
        {
            payloads = new[] { single };
            return true;
        }

        if (TryCollectFileChangePayloadsFromDiffText(item, out var fromDiff))
        {
            payloads = fromDiff;
            return true;
        }

        return false;
    }

    private static bool TryCollectFileChangePayloadFromSingle(JsonElement item, out FileChangePayload payload)
    {
        payload = default;

        if (!TryGetFileChangePayload(item, out var path, out var diff, out var added, out var removed))
        {
            return false;
        }

        payload = new FileChangePayload(path, diff, added, removed);
        return true;
    }

    private static bool TryCollectFileChangePayloadsFromContainer(JsonElement item, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var list = new List<FileChangePayload>(capacity: 4);
        foreach (var propertyName in new[] { "files", "fileChanges", "file_changes", "changes" })
        {
            if (!item.TryGetProperty(propertyName, out var container) || container.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in container.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetFileChangePayload(entry, out var path, out var diff, out var added, out var removed))
                {
                    continue;
                }

                list.Add(new FileChangePayload(path, diff, added, removed));
            }
        }

        if (list.Count == 0)
        {
            return false;
        }

        payloads = list;
        return true;
    }

    private static bool TryCollectFileChangePayloadsFromDiffText(JsonElement item, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        if (!TryGetStringAny(item, out var diffValue, "diff", "patch", "unifiedDiff", "unified_diff", "diffText", "diff_text"))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(diffValue))
        {
            return false;
        }

        if (!TrySplitPatchByFile(diffValue, out var splitPayloads))
        {
            return false;
        }

        payloads = splitPayloads;
        return true;
    }

    private static bool TrySplitPatchByFile(string diffText, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        if (string.IsNullOrWhiteSpace(diffText))
        {
            return false;
        }

        if (diffText.Contains("*** Begin Patch", StringComparison.Ordinal) || diffText.Contains("*** Update File:", StringComparison.Ordinal))
        {
            if (TrySplitApplyPatch(diffText, out payloads))
            {
                return true;
            }
        }

        if (diffText.Contains("diff --git ", StringComparison.Ordinal))
        {
            if (TrySplitGitDiff(diffText, out payloads))
            {
                return true;
            }
        }

        if (TryExtractSinglePathFromDiffText(diffText, out var path))
        {
            payloads = new[] { new FileChangePayload(path, diffText, null, null) };
            return true;
        }

        return false;
    }

    private static bool TrySplitApplyPatch(string diffText, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        var normalized = diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var list = new List<FileChangePayload>(capacity: 2);
        var currentPath = (string?)null;
        var currentBuilder = (StringBuilder?)null;

        void Flush()
        {
            if (currentBuilder is null || string.IsNullOrWhiteSpace(currentPath))
            {
                return;
            }

            var text = currentBuilder.ToString().TrimEnd('\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            list.Add(new FileChangePayload(currentPath.Trim(), text, null, null));
        }

        foreach (var line in lines)
        {
            if (TryParseApplyPatchFileHeader(line, out var path))
            {
                Flush();
                currentPath = path;
                currentBuilder = new StringBuilder(capacity: Math.Min(diffText.Length, 4096));
            }

            if (currentBuilder is not null)
            {
                currentBuilder.Append(line);
                currentBuilder.Append('\n');
            }
        }

        Flush();

        if (list.Count == 0)
        {
            return false;
        }

        payloads = list;
        return true;
    }

    private static bool TryParseApplyPatchFileHeader(string line, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();

        const string updatePrefix = "*** Update File:";
        const string addPrefix = "*** Add File:";
        const string deletePrefix = "*** Delete File:";

        if (trimmed.StartsWith(updatePrefix, StringComparison.Ordinal))
        {
            path = trimmed.Substring(updatePrefix.Length).TrimStart();
            return !string.IsNullOrWhiteSpace(path);
        }

        if (trimmed.StartsWith(addPrefix, StringComparison.Ordinal))
        {
            path = trimmed.Substring(addPrefix.Length).TrimStart();
            return !string.IsNullOrWhiteSpace(path);
        }

        if (trimmed.StartsWith(deletePrefix, StringComparison.Ordinal))
        {
            path = trimmed.Substring(deletePrefix.Length).TrimStart();
            return !string.IsNullOrWhiteSpace(path);
        }

        return false;
    }

    private static bool TrySplitGitDiff(string diffText, out IReadOnlyList<FileChangePayload> payloads)
    {
        payloads = Array.Empty<FileChangePayload>();

        var normalized = diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var list = new List<FileChangePayload>(capacity: 2);
        var currentPath = (string?)null;
        var currentBuilder = (StringBuilder?)null;

        void Flush()
        {
            if (currentBuilder is null || string.IsNullOrWhiteSpace(currentPath))
            {
                return;
            }

            var text = currentBuilder.ToString().TrimEnd('\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            list.Add(new FileChangePayload(currentPath.Trim(), text, null, null));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && TryParseGitDiffHeaderPath(line, out var path))
            {
                Flush();
                currentPath = path;
                currentBuilder = new StringBuilder(capacity: Math.Min(diffText.Length, 4096));
            }

            if (currentBuilder is not null)
            {
                currentBuilder.Append(line);
                currentBuilder.Append('\n');
            }
        }

        Flush();

        if (list.Count == 0)
        {
            return false;
        }

        payloads = list;
        return true;
    }

    private static bool TryParseGitDiffHeaderPath(string line, out string path)
    {
        path = string.Empty;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        var aPath = NormalizeGitDiffPath(parts[2]);
        var bPath = NormalizeGitDiffPath(parts[3]);

        if (!string.IsNullOrWhiteSpace(bPath))
        {
            path = bPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(aPath))
        {
            path = aPath;
            return true;
        }

        return false;
    }

    private static string? NormalizeGitDiffPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, "/dev/null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            return trimmed.Substring(2);
        }

        return trimmed;
    }

    private static bool TryExtractSinglePathFromDiffText(string diffText, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(diffText))
        {
            return false;
        }

        var normalized = diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        foreach (var line in lines)
        {
            if (TryParseApplyPatchFileHeader(line, out path))
            {
                return true;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && TryParseGitDiffHeaderPath(line, out path))
            {
                return true;
            }

            if (TryParseUnifiedDiffHeaderPath(line, out path))
            {
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static bool TryParseUnifiedDiffHeaderPath(string line, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("+++ ", StringComparison.Ordinal) || trimmed.StartsWith("--- ", StringComparison.Ordinal))
        {
            var candidate = trimmed.Substring(4).Trim();
            candidate = NormalizeGitDiffPath(candidate);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFileChangePayload(
        JsonElement item,
        out string path,
        out string? diff,
        out int? added,
        out int? removed)
    {
        path = string.Empty;
        diff = null;
        added = null;
        removed = null;

        if (!TryGetStringAny(item, out var pathValue, "path", "filePath", "file_path", "relativePath", "relative_path", "filename", "file"))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        path = pathValue.Trim();

        if (TryGetStringAny(item, out var diffValue, "diff", "patch", "unifiedDiff", "unified_diff", "diffText", "diff_text"))
        {
            diff = diffValue;
        }

        if (TryGetInt32Any(item, out var addedValue, "added", "additions", "addedLines", "added_lines"))
        {
            added = addedValue;
        }

        if (TryGetInt32Any(item, out var removedValue, "removed", "deletions", "removedLines", "removed_lines"))
        {
            removed = removedValue;
        }

        return true;
    }

    private static bool TryGetFileChangePayloadsFromApprovalParams(
        JsonElement @params,
        out IReadOnlyList<FileChangePayload> payloads)
    {
        if (TryGetFileChangePayloads(@params, out payloads))
        {
            return true;
        }

        if (@params.ValueKind == JsonValueKind.Object)
        {
            if (@params.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                if (TryGetFileChangePayloads(item, out payloads))
                {
                    return true;
                }
            }

            if (@params.TryGetProperty("fileChange", out var fileChange) && fileChange.ValueKind == JsonValueKind.Object)
            {
                if (TryGetFileChangePayloads(fileChange, out payloads))
                {
                    return true;
                }
            }
        }

        payloads = Array.Empty<FileChangePayload>();
        return false;
    }

    private static BridgeEnvelope CreateEvent(string name, object data) =>
        new()
        {
            Type = "event",
            Name = name,
            Ts = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(data, BridgeJson.SerializerOptions),
        };
}

public sealed class CodexAppServerTurnResult
{
    public required string ThreadId { get; init; }

    public required string TurnId { get; init; }

    public required string Status { get; init; }

    public required JsonElement Turn { get; init; }
}
