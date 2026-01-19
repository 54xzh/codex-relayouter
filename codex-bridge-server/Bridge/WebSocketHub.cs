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

    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly BridgeRequestAuthorizer _authorizer;
    private readonly CodexAppServerRunner _appServerRunner;
    private readonly CodexSessionStore _sessionStore;
    private readonly ILogger<WebSocketHub> _logger;

    private CancellationTokenSource? _currentRunCts;
    private readonly object _approvalGate = new();
    private TaskCompletionSource<CodexAppServerApprovalDecision>? _pendingApprovalTcs;
    private string? _pendingApprovalRequestId;
    private string? _pendingApprovalRunId;

    public WebSocketHub(
        BridgeRequestAuthorizer authorizer,
        CodexAppServerRunner appServerRunner,
        CodexSessionStore sessionStore,
        ILogger<WebSocketHub> logger)
    {
        _authorizer = authorizer;
        _appServerRunner = appServerRunner;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!_authorizer.IsAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString("N");

        _clients[clientId] = webSocket;
        _logger.LogInformation("WebSocket 已连接: {ClientId}", clientId);

        try
        {
            await SendAsync(webSocket, CreateEvent("bridge.connected", new { clientId }), context.RequestAborted);
            await ReceiveLoopAsync(clientId, webSocket, context.RequestAborted);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogInformation("WebSocket 已断开: {ClientId}", clientId);
        }
    }

    private async Task ReceiveLoopAsync(string clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var segment = new ArraySegment<byte>(buffer);

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
                await HandleRunCancelAsync(clientId, cancellationToken);
                break;
            case "approval.respond":
                await HandleApprovalRespondAsync(clientId, envelope, cancellationToken);
                break;
        }
    }

    private async Task HandleChatSendAsync(string clientId, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_currentRunCts is not null)
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "已有运行中的任务" }), cancellationToken);
            return;
        }

        TryGetString(envelope.Data, "prompt", out var prompt);
        prompt = prompt?.Trim() ?? string.Empty;

        var images = ParseImageDataUrls(envelope.Data);

        if (string.IsNullOrWhiteSpace(prompt) && (images is null || images.Length == 0))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "缺少 prompt/images" }), cancellationToken);
            return;
        }

        TryGetString(envelope.Data, "sessionId", out var sessionId);
        TryGetString(envelope.Data, "workingDirectory", out var workingDirectory);
        TryGetString(envelope.Data, "model", out var model);
        TryGetString(envelope.Data, "sandbox", out var sandbox);
        TryGetString(envelope.Data, "approvalPolicy", out var approvalPolicy);
        TryGetString(envelope.Data, "effort", out var effort);
        TryGetBoolean(envelope.Data, "skipGitRepoCheck", out var skipGitRepoCheck);
        var runId = Guid.NewGuid().ToString("N");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentRunCts = cts;

        await BroadcastAsync(CreateEvent("chat.message", new { runId, role = "user", text = prompt, images, clientId }), cancellationToken);
        await BroadcastAsync(CreateEvent("run.started", new { runId, clientId }), cancellationToken);

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
                    BroadcastAsync,
                    (approval, ct) => RequestApprovalAsync(runId, approval, ct),
                    cts.Token);

                var status = result.Status?.Trim();
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    await BroadcastAsync(CreateEvent("run.completed", new { runId, exitCode = 0 }), cts.Token);
                    return;
                }

                if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase))
                {
                    await BroadcastAsync(CreateEvent("run.canceled", new { runId }), CancellationToken.None);
                    return;
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var message = TryGetTurnFailureMessage(result.Turn) ?? "codex 执行失败";
                    await BroadcastAsync(CreateEvent("run.failed", new { runId, message }), CancellationToken.None);
                    return;
                }

                await BroadcastAsync(CreateEvent("run.completed", new { runId, exitCode = 0 }), cts.Token);
            }
            catch (OperationCanceledException)
            {
                await BroadcastAsync(CreateEvent("run.canceled", new { runId }), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "运行 codex 失败");
                await BroadcastAsync(CreateEvent("run.failed", new { runId, message = ex.Message }), CancellationToken.None);
            }
            finally
            {
                _currentRunCts = null;

                lock (_approvalGate)
                {
                    _pendingApprovalTcs = null;
                    _pendingApprovalRequestId = null;
                    _pendingApprovalRunId = null;
                }

                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task HandleRunCancelAsync(string clientId, CancellationToken cancellationToken)
    {
        if (_currentRunCts is null)
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "没有可取消的任务" }), cancellationToken);
            return;
        }

        _currentRunCts.Cancel();
        await BroadcastAsync(CreateEvent("run.cancel.requested", new { clientId }), cancellationToken);
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

        TaskCompletionSource<CodexAppServerApprovalDecision> tcs;

        lock (_approvalGate)
        {
            if (_pendingApprovalTcs is not null)
            {
                _pendingApprovalTcs.TrySetResult(new CodexAppServerApprovalDecision { Decision = "decline" });
            }

            tcs = new TaskCompletionSource<CodexAppServerApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingApprovalTcs = tcs;
            _pendingApprovalRequestId = approval.RequestId;
            _pendingApprovalRunId = runId;
        }

        await BroadcastAsync(
            CreateEvent(
                "approval.requested",
                new
                {
                    runId,
                    requestId = approval.RequestId,
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
        return await tcs.Task;
    }

    private async Task HandleApprovalRespondAsync(string clientId, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        TryGetString(envelope.Data, "runId", out var runId);

        if (!TryGetString(envelope.Data, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            await BroadcastAsync(CreateEvent("run.rejected", new { reason = "缺少 requestId" }), cancellationToken);
            return;
        }

        if (!TryGetString(envelope.Data, "decision", out var decision) || string.IsNullOrWhiteSpace(decision))
        {
            decision = "decline";
        }

        TaskCompletionSource<CodexAppServerApprovalDecision>? tcs = null;

        lock (_approvalGate)
        {
            if (_pendingApprovalTcs is null)
            {
                return;
            }

            if (!string.Equals(_pendingApprovalRequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingApprovalRunId)
                && !string.IsNullOrWhiteSpace(runId)
                && !string.Equals(_pendingApprovalRunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            tcs = _pendingApprovalTcs;
            _pendingApprovalTcs = null;
            _pendingApprovalRequestId = null;
            _pendingApprovalRunId = null;
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
        foreach (var (clientId, socket) in clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "向客户端发送失败: {ClientId}", clientId);
            }
        }
    }

    private static Task SendAsync(WebSocket socket, BridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(envelope, BridgeJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        return SendAsyncCore(socket, bytes, cancellationToken);
    }

    private static async Task SendAsyncCore(WebSocket socket, byte[] bytes, CancellationToken cancellationToken)
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
