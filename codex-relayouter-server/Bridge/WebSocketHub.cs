// WebSocketHub：处理 /ws 连接、命令分发，并广播来自 CodexRunner 的流式事件。
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace codex_bridge_server.Bridge;

public sealed class WebSocketHub
{
    private const int MaxInputImages = 4;

    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly BridgeRequestAuthorizer _authorizer;
    private readonly CodexAppServerRunner _appServerRunner;
    private readonly CodexSessionStore _sessionStore;
    private readonly DevicePresenceTracker _presenceTracker;
    private readonly ILogger<WebSocketHub> _logger;

    private readonly ConcurrentDictionary<string, RunContext> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeRunBySessionId = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<(string runId, string requestId), TaskCompletionSource<CodexAppServerApprovalDecision>> _pendingApprovals = new();

    public WebSocketHub(
        BridgeRequestAuthorizer authorizer,
        CodexAppServerRunner appServerRunner,
        CodexSessionStore sessionStore,
        DevicePresenceTracker presenceTracker,
        ILogger<WebSocketHub> logger)
    {
        _authorizer = authorizer;
        _appServerRunner = appServerRunner;
        _sessionStore = sessionStore;
        _presenceTracker = presenceTracker;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var auth = _authorizer.Authorize(context);
        if (!auth.IsAuthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString("N");

        _clients[clientId] = new ClientConnection(webSocket, auth.IsLoopback, auth.DeviceId);
        _presenceTracker.TrackClient(clientId, auth.DeviceId);
        _logger.LogInformation("WebSocket 已连接: {ClientId}", clientId);

        try
        {
            await SendAsync(_clients[clientId], CreateEvent("bridge.connected", new { clientId }), context.RequestAborted);
            if (!string.IsNullOrWhiteSpace(auth.DeviceId))
            {
                await BroadcastToLoopbackAsync(
                    CreateEvent("device.presence.updated", new { deviceId = auth.DeviceId, online = true, lastSeenAt = DateTimeOffset.UtcNow }),
                    context.RequestAborted);
            }
            await ReceiveLoopAsync(clientId, webSocket, context.RequestAborted);
        }
        finally
        {
            if (_clients.TryRemove(clientId, out var removed))
            {
                _presenceTracker.UntrackClient(clientId);

                if (!string.IsNullOrWhiteSpace(removed.DeviceId))
                {
                    await BroadcastToLoopbackAsync(
                        CreateEvent("device.presence.updated", new { deviceId = removed.DeviceId, online = false, lastSeenAt = DateTimeOffset.UtcNow }),
                        CancellationToken.None);
                }
            }
            _logger.LogInformation("WebSocket 已断开: {ClientId}", clientId);
        }
    }

    public Task NotifyPairingRequestedAsync(PairingRequestNotification notification, CancellationToken cancellationToken)
    {
        return BroadcastToLoopbackAsync(
            CreateEvent(
                "device.pairing.requested",
                new
                {
                    requestId = notification.RequestId,
                    deviceName = notification.DeviceName,
                    platform = notification.Platform,
                    deviceModel = notification.DeviceModel,
                    appVersion = notification.AppVersion,
                    clientIp = notification.ClientIp,
                    expiresAt = notification.ExpiresAt,
                }),
            cancellationToken);
    }

    public async Task DisconnectDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var targetId = deviceId.Trim();
        var clients = _clients.ToArray();
        foreach (var (clientId, connection) in clients)
        {
            if (string.IsNullOrWhiteSpace(connection.DeviceId)
                || !string.Equals(connection.DeviceId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (connection.Socket.State == WebSocketState.Open)
                {
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "device revoked", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "断开设备连接失败: {ClientId}", clientId);
            }
        }
    }

    private async Task ReceiveLoopAsync(string clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var segment = new ArraySegment<byte>(buffer);

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult? result;

                do
                {
                    result = await socket.ReceiveAsync(segment, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken);
                        return;
                    }

                    messageStream.Write(segment.Array!, segment.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(messageStream.ToArray());
                await HandleClientMessageAsync(clientId, text, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket 接收中断: {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket 接收失败: {ClientId}", clientId);
        }

    }

    private async Task HandleClientMessageAsync(string clientId, string text, CancellationToken cancellationToken)
    {
        BridgeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BridgeEnvelope>(text, BridgeJson.SerializerOptions);
        }
        catch (JsonException)
        {
            await BroadcastAsync(CreateEvent("bridge.error", new { message = "无效 JSON" }), cancellationToken);
            return;
        }

        if (envelope is null)
        {
            await BroadcastAsync(CreateEvent("bridge.error", new { message = "空消息" }), cancellationToken);
            return;
        }

        if (!string.Equals(envelope.Type, "command", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (envelope.Name)
        {
            case "chat.send":
                await HandleChatSendAsync(clientId, envelope, cancellationToken);
                break;
            case "run.cancel":
                await HandleRunCancelAsync(clientId, envelope, cancellationToken);
                break;
            case "approval.respond":
                await HandleApprovalRespondAsync(clientId, envelope, cancellationToken);
                break;
        }
    }

    private async Task HandleChatSendAsync(string clientId, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        TryGetString(envelope.Data, "prompt", out var prompt);
        prompt = prompt?.Trim() ?? string.Empty;

        var images = ParseImageDataUrls(envelope.Data);

        if (string.IsNullOrWhiteSpace(prompt) && (images is null || images.Length == 0))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "缺少 prompt/images" }), cancellationToken);
            return;
        }

        TryGetString(envelope.Data, "sessionId", out var sessionId);
        sessionId = NormalizeSessionId(sessionId);
        TryGetString(envelope.Data, "workingDirectory", out var workingDirectory);
        TryGetString(envelope.Data, "model", out var model);
        TryGetString(envelope.Data, "sandbox", out var sandbox);
        TryGetString(envelope.Data, "approvalPolicy", out var approvalPolicy);
        TryGetString(envelope.Data, "effort", out var effort);
        TryGetBoolean(envelope.Data, "skipGitRepoCheck", out var skipGitRepoCheck);
        var runId = Guid.NewGuid().ToString("N");

        if (string.IsNullOrWhiteSpace(sandbox) || string.IsNullOrWhiteSpace(approvalPolicy))
        {
            var (defaultApprovalPolicy, defaultSandbox) = ResolveDefaultRunSettings(sessionId);
            sandbox = string.IsNullOrWhiteSpace(sandbox) ? defaultSandbox : sandbox;
            approvalPolicy = string.IsNullOrWhiteSpace(approvalPolicy) ? defaultApprovalPolicy : approvalPolicy;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = new RunContext(runId, clientId, sessionId, cts);
        _runs[runId] = run;

        if (!string.IsNullOrWhiteSpace(sessionId) && !_activeRunBySessionId.TryAdd(sessionId, runId))
        {
            _runs.TryRemove(runId, out _);
            cts.Dispose();
            await BroadcastAsync(CreateEvent("run.rejected", new { clientId, sessionId, reason = "该会话已有运行中的任务" }), cancellationToken);
            return;
        }

        await BroadcastAsync(CreateEvent("chat.message", new { runId, sessionId, role = "user", text = prompt, images, clientId }), cancellationToken);
        await BroadcastAsync(CreateEvent("run.started", new { runId, sessionId, clientId }), cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    _sessionStore.EnsureSessionCwd(sessionId, workingDirectory);
                }

                var result = await _appServerRunner.RunTurnAsync(
                    runId,
                    new CodexRunRequest
                    {
                        Prompt = prompt,
                        Images = images,
                        SessionId = sessionId,
                        WorkingDirectory = workingDirectory,
                        Model = model,
                        Sandbox = sandbox,
                        SkipGitRepoCheck = skipGitRepoCheck,
                        ApprovalPolicy = approvalPolicy,
                        Effort = effort,
                    },
                    (evt, ct) => EmitRunEventAsync(run, evt, ct),
                    (approval, ct) => RequestApprovalAsync(runId, approval, ct),
                    cts.Token);

                var status = result.Status?.Trim();
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    await BroadcastAsync(CreateEvent("run.completed", new { runId, sessionId = run.SessionId, exitCode = 0 }), cts.Token);
                    return;
                }

                if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase))
                {
                    await BroadcastAsync(CreateEvent("run.canceled", new { runId, sessionId = run.SessionId }), CancellationToken.None);
                    return;
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var message = TryGetTurnFailureMessage(result.Turn) ?? "codex 执行失败";
                    await BroadcastAsync(CreateEvent("run.failed", new { runId, sessionId = run.SessionId, message }), CancellationToken.None);
                    return;
                }

                await BroadcastAsync(CreateEvent("run.completed", new { runId, sessionId = run.SessionId, exitCode = 0 }), cts.Token);
            }
            catch (OperationCanceledException)
            {
                await BroadcastAsync(CreateEvent("run.canceled", new { runId, sessionId = run.SessionId }), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "运行 codex 失败");
                await BroadcastAsync(CreateEvent("run.failed", new { runId, sessionId = run.SessionId, message = ex.Message }), CancellationToken.None);
            }
            finally
            {
                _runs.TryRemove(runId, out _);

                var activeSessionId = run.SessionId;
                if (!string.IsNullOrWhiteSpace(activeSessionId)
                    && _activeRunBySessionId.TryGetValue(activeSessionId, out var activeRunId)
                    && string.Equals(activeRunId, runId, StringComparison.Ordinal))
                {
                    _activeRunBySessionId.TryRemove(activeSessionId, out _);
                }

                foreach (var entry in _pendingApprovals)
                {
                    if (!string.Equals(entry.Key.runId, runId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (_pendingApprovals.TryRemove(entry.Key, out var tcs))
                    {
                        tcs.TrySetResult(new CodexAppServerApprovalDecision { Decision = "decline" });
                    }
                }

                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task HandleRunCancelAsync(string clientId, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        TryGetString(envelope.Data, "runId", out var runId);
        runId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();

        TryGetString(envelope.Data, "sessionId", out var sessionId);
        sessionId = NormalizeSessionId(sessionId);

        if (string.IsNullOrWhiteSpace(runId) && string.IsNullOrWhiteSpace(sessionId))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { clientId, reason = "缺少 runId/sessionId" }), cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(runId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            _activeRunBySessionId.TryGetValue(sessionId, out runId);
        }

        if (string.IsNullOrWhiteSpace(runId) || !_runs.TryGetValue(runId, out var run))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { clientId, sessionId, reason = "没有可取消的任务" }), cancellationToken);
            return;
        }

        run.Cts.Cancel();
        await BroadcastAsync(CreateEvent("run.cancel.requested", new { clientId, runId, sessionId = run.SessionId ?? sessionId }), cancellationToken);
    }

    private Task EmitRunEventAsync(RunContext run, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(envelope.Name, "session.created", StringComparison.OrdinalIgnoreCase)
                && TryGetString(envelope.Data, "sessionId", out var sessionId))
            {
                sessionId = NormalizeSessionId(sessionId);
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    run.SessionId = sessionId;
                    _activeRunBySessionId.TryAdd(sessionId, run.RunId);
                }
            }

            if (string.Equals(envelope.Name, "turn.started", StringComparison.OrdinalIgnoreCase)
                && TryGetString(envelope.Data, "threadId", out var threadId))
            {
                threadId = NormalizeSessionId(threadId);
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    run.SessionId = threadId;
                    _activeRunBySessionId.TryAdd(threadId, run.RunId);
                }
            }
        }

        return BroadcastAsync(envelope, cancellationToken);
    }

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();

    private (string? approvalPolicy, string? sandbox) ResolveDefaultRunSettings(string? sessionId)
    {
        string? approvalPolicy = null;
        string? sandbox = null;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var snapshot = _sessionStore.TryReadLatestSettings(sessionId);
            approvalPolicy = snapshot?.ApprovalPolicy;
            sandbox = snapshot?.Sandbox;
        }

        if (approvalPolicy is null || sandbox is null)
        {
            if (CodexCliConfig.TryLoadApprovalPolicyAndSandboxMode(out var configApprovalPolicy, out var configSandbox))
            {
                approvalPolicy ??= configApprovalPolicy;
                sandbox ??= configSandbox;
            }
        }

        return (approvalPolicy, sandbox);
    }

    private async Task<CodexAppServerApprovalDecision> RequestApprovalAsync(
        string runId,
        CodexAppServerApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        if (_clients.IsEmpty)
        {
            return new CodexAppServerApprovalDecision { Decision = "decline" };
        }

        var requestId = approval.RequestId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return new CodexAppServerApprovalDecision { Decision = "decline" };
        }

        var key = (runId, requestId);
        var tcs = new TaskCompletionSource<CodexAppServerApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingApprovals.AddOrUpdate(
            key,
            tcs,
            (_, existing) =>
            {
                existing.TrySetResult(new CodexAppServerApprovalDecision { Decision = "decline" });
                return tcs;
            });

        await BroadcastAsync(
            CreateEvent(
                "approval.requested",
                new
                {
                    runId,
                    requestId,
                    kind = approval.Kind,
                    threadId = approval.ThreadId,
                    turnId = approval.TurnId,
                    itemId = approval.ItemId,
                    reason = approval.Reason,
                    proposedExecpolicyAmendment = approval.ProposedExecpolicyAmendment,
                    grantRoot = approval.GrantRoot,
                }),
            cancellationToken);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pendingApprovals.TryRemove(key, out _);
        }
    }

    private async Task HandleApprovalRespondAsync(string clientId, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!TryGetString(envelope.Data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { clientId, reason = "缺少 runId" }), cancellationToken);
            return;
        }

        runId = runId.Trim();

        if (!TryGetString(envelope.Data, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "缺少 requestId" }), cancellationToken);
            return;
        }

        requestId = requestId.Trim();

        if (!TryGetString(envelope.Data, "decision", out var decision) || string.IsNullOrWhiteSpace(decision))
        {
            decision = "decline";
        }

        var key = (runId, requestId);
        if (!_pendingApprovals.TryRemove(key, out var tcs))
        {
            return;
        }

        tcs.TrySetResult(new CodexAppServerApprovalDecision { Decision = decision });

        await BroadcastAsync(CreateEvent("approval.responded", new { clientId, requestId, decision }), cancellationToken);
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

    private static bool TryHandleCodexJsonEvent(string line, string runId, out BridgeEnvelope? envelope)
    {
        envelope = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGetString(root, "type", out var type) || string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            if (string.Equals(type, "thread.started", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetString(root, "thread_id", out var threadId) || string.IsNullOrWhiteSpace(threadId))
                {
                    return true;
                }

                envelope = CreateEvent("session.created", new { runId, sessionId = threadId });
                return true;
            }

            if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                if (!TryGetString(item, "type", out var itemType) || string.IsNullOrWhiteSpace(itemType))
                {
                    return true;
                }

                if (string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetString(item, "text", out var text) || string.IsNullOrWhiteSpace(text))
                    {
                        return true;
                    }

                    envelope = CreateEvent("chat.message", new { runId, role = "assistant", text });
                    return true;
                }

                if (string.Equals(itemType, "command_execution", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetString(item, "id", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                    {
                        return true;
                    }

                    if (!TryGetString(item, "command", out var command) || string.IsNullOrWhiteSpace(command))
                    {
                        return true;
                    }

                    TryGetString(item, "status", out var status);
                    TryGetString(item, "aggregated_output", out var output);
                    var hasExitCode = TryGetInt32(item, "exit_code", out var exitCode);

                    envelope = CreateEvent(
                        "run.command",
                        new
                        {
                            runId,
                            itemId,
                            command,
                            status = string.IsNullOrWhiteSpace(status) ? "completed" : status,
                            exitCode = hasExitCode ? exitCode : (int?)null,
                            output = string.IsNullOrWhiteSpace(output) ? null : output,
                        });
                    return true;
                }

                if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetString(item, "id", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                    {
                        return true;
                    }

                    if (!TryGetString(item, "text", out var text) || string.IsNullOrWhiteSpace(text))
                    {
                        return true;
                    }

                    envelope = CreateEvent("run.reasoning", new { runId, itemId, text });
                    return true;
                }

                return true;
            }

            if (string.Equals(type, "item.started", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                if (!TryGetString(item, "type", out var itemType) || string.IsNullOrWhiteSpace(itemType))
                {
                    return true;
                }

                if (!string.Equals(itemType, "command_execution", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!TryGetString(item, "id", out var itemId) || string.IsNullOrWhiteSpace(itemId))
                {
                    return true;
                }

                if (!TryGetString(item, "command", out var command) || string.IsNullOrWhiteSpace(command))
                {
                    return true;
                }

                TryGetString(item, "status", out var status);

                envelope = CreateEvent(
                    "run.command",
                    new
                    {
                        runId,
                        itemId,
                        command,
                        status = string.IsNullOrWhiteSpace(status) ? "in_progress" : status,
                    });
                return true;
            }

            // 其他 codex --json 事件（turn.started/turn.completed/...）不向前端展示。
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static string? TryGetTurnFailureMessage(JsonElement turn)
    {
        if (turn.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!turn.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            var text = error.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(error, "message", out var message) && !string.IsNullOrWhiteSpace(message)
            ? message.Trim()
            : null;
    }

    private static string[]? ParseImageDataUrls(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.TryGetProperty("images", out var imagesProp) || imagesProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>(capacity: Math.Min(MaxInputImages, 4));
        foreach (var item in imagesProp.EnumerateArray())
        {
            string? url = null;

            if (item.ValueKind == JsonValueKind.String)
            {
                url = item.GetString();
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (TryGetString(item, "dataUrl", out var dataUrl))
                {
                    url = dataUrl;
                }
            }

            url = url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(url);
            if (list.Count >= MaxInputImages)
            {
                break;
            }
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    private static bool TryGetBoolean(JsonElement data, string propertyName, out bool value)
    {
        value = false;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
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

    private static BridgeEnvelope CreateEvent(string name, object data) =>
        new()
        {
            Type = "event",
            Name = name,
            Ts = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(data, BridgeJson.SerializerOptions),
        };

    private async Task BroadcastAsync(BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(envelope, BridgeJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var segment = new ArraySegment<byte>(bytes);

        var clients = _clients.ToArray();
        foreach (var (clientId, connection) in clients)
        {
            var socket = connection.Socket;
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await SendAsync(connection, segment, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "向客户端发送失败: {ClientId}", clientId);
            }
        }
    }

    private async Task BroadcastToLoopbackAsync(BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(envelope, BridgeJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var segment = new ArraySegment<byte>(bytes);

        var clients = _clients.ToArray();
        foreach (var (clientId, connection) in clients)
        {
            if (!connection.IsLoopback)
            {
                continue;
            }

            var socket = connection.Socket;
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await SendAsync(connection, segment, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "向本机客户端发送失败: {ClientId}", clientId);
            }
        }
    }

    private static async Task SendAsync(ClientConnection connection, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(envelope, BridgeJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await SendAsync(connection, new ArraySegment<byte>(bytes), cancellationToken);
    }

    private static async Task SendAsync(ClientConnection connection, ArraySegment<byte> segment, CancellationToken cancellationToken)
    {
        await connection.SendGate.WaitAsync(cancellationToken);
        try
        {
            await connection.Socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            connection.SendGate.Release();
        }
    }

    private sealed class RunContext
    {
        public RunContext(string runId, string clientId, string? sessionId, CancellationTokenSource cts)
        {
            RunId = runId;
            ClientId = clientId;
            SessionId = sessionId;
            Cts = cts;
        }

        public string RunId { get; }

        public string ClientId { get; }

        public string? SessionId { get; set; }

        public CancellationTokenSource Cts { get; }
    }

    private sealed class ClientConnection
    {
        public ClientConnection(WebSocket socket, bool isLoopback, string? deviceId)
        {
            Socket = socket;
            IsLoopback = isLoopback;
            DeviceId = deviceId;
        }

        public WebSocket Socket { get; }

        public bool IsLoopback { get; }

        public string? DeviceId { get; }

        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
