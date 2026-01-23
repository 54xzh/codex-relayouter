// ChatSessionStore：在 WinUI 侧缓存每个会话的流式输出、计划与运行状态，支持切换页面/会话时不中断展示。
using codex_bridge.Bridge;
using codex_bridge.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace codex_bridge.State;

public enum SessionIndicatorState
{
    None = 0,
    Running = 1,
    Completed = 2,
    Warning = 3,
}

public sealed class ChatSessionState
{
    public ChatSessionState(string sessionKey)
    {
        SessionKey = sessionKey;
    }

    public string SessionKey { get; }

    public string? WorkingDirectoryOverride { get; set; }

    public string? SandboxOverride { get; set; }

    public string? ApprovalPolicyOverride { get; set; }

    public string? ModelOverride { get; set; }

    public string? EffortOverride { get; set; }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<TurnPlanStepViewModel> TurnPlanSteps { get; } = new();

    public string? TurnPlanExplanation { get; set; }

    public DateTimeOffset? TurnPlanUpdatedAt { get; set; }

    public string? TurnPlanTurnId { get; set; }

    public bool HasLoadedHistory { get; set; }

    public bool HasLoadedPlan { get; set; }

    public string? ActiveRunId { get; set; }

    public bool HasCompletedBadge { get; set; }

    public bool HasWarningBadge { get; set; }

    public SessionIndicatorState Indicator
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ActiveRunId))
            {
                return SessionIndicatorState.Running;
            }

            if (HasWarningBadge)
            {
                return SessionIndicatorState.Warning;
            }

            if (HasCompletedBadge)
            {
                return SessionIndicatorState.Completed;
            }

            return SessionIndicatorState.None;
        }
    }
}

public sealed class ChatSessionStore
{
    public const string NewChatSessionKey = "__newchat__";

    private readonly Dictionary<string, ChatSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _runToSessionKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _runToClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatMessageViewModel> _runToMessage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiffSummarySnapshot> _pendingDiffSummaries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRuns = new(StringComparer.Ordinal);
    private DispatcherQueue? _dispatcherQueue;
    private string? _localClientId;
    private bool _chatPageActive;

    public event EventHandler<string>? SessionIndicatorChanged;
    public event EventHandler<string>? SessionContentUpdated;
    public event EventHandler<string>? SessionPlanUpdated;
    public event EventHandler<string>? SessionRunStateChanged;

    public void Initialize(DispatcherQueue dispatcherQueue)
    {
        if (_dispatcherQueue is not null)
        {
            return;
        }

        _dispatcherQueue = dispatcherQueue;
        App.SessionState.CurrentSessionChanged += (_, _) => OnCurrentSessionChanged();
    }

    public void Attach(ConnectionService connectionService)
    {
        connectionService.EnvelopeReceived += ConnectionService_EnvelopeReceived;
        connectionService.ConnectionClosed += (_, _) =>
        {
            _localClientId = null;
        };
    }

    public void SetChatPageActive(bool isActive)
    {
        _chatPageActive = isActive;
        if (isActive)
        {
            OnCurrentSessionChanged();
        }
    }

    public ChatSessionState GetSessionState(string? sessionId)
    {
        var key = NormalizeSessionKey(sessionId);
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new ChatSessionState(key);
        _sessions[key] = created;
        return created;
    }

    public string? GetActiveRunId(string? sessionId)
    {
        var key = NormalizeSessionKey(sessionId);
        return _sessions.TryGetValue(key, out var state) ? state.ActiveRunId : null;
    }

    public SessionIndicatorState GetIndicator(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionIndicatorState.None;
        }

        return GetSessionState(sessionId).Indicator;
    }

    private void ConnectionService_EnvelopeReceived(object? sender, BridgeEnvelope envelope)
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            try
            {
                HandleEnvelopeOnUiThread(envelope);
            }
            catch
            {
            }
        });
    }

    private void HandleEnvelopeOnUiThread(BridgeEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (envelope.Name)
        {
            case "bridge.connected":
                HandleBridgeConnected(envelope.Data);
                return;
            case "run.started":
                HandleRunStarted(envelope.Data);
                return;
            case "session.created":
                HandleSessionCreated(envelope.Data);
                return;
            case "turn.started":
                HandleTurnStarted(envelope.Data);
                return;
            case "chat.message":
                HandleChatMessage(envelope.Data);
                return;
            case "chat.message.delta":
                HandleChatMessageDelta(envelope.Data);
                return;
            case "run.command":
                HandleRunCommand(envelope.Data);
                return;
            case "run.command.outputDelta":
                HandleRunCommandOutputDelta(envelope.Data);
                return;
            case "run.reasoning":
                HandleRunReasoning(envelope.Data);
                return;
            case "run.reasoning.delta":
                HandleRunReasoningDelta(envelope.Data);
                return;
            case "diff.updated":
                HandleDiffUpdated(envelope.Data);
                return;
            case "diff.summary":
                HandleDiffSummary(envelope.Data);
                return;
            case "run.plan.updated":
                HandleRunPlanUpdated(envelope.Data);
                return;
            case "run.completed":
                HandleRunCompleted(envelope.Data, succeeded: true);
                return;
            case "run.failed":
                HandleRunCompleted(envelope.Data, succeeded: false);
                return;
            case "run.canceled":
                HandleRunCanceled(envelope.Data);
                return;
            case "run.rejected":
                HandleRunRejected(envelope.Data);
                return;
        }
    }

    private void HandleBridgeConnected(JsonElement data)
    {
        if (TryGetString(data, "clientId", out var clientId))
        {
            _localClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        }
    }

    private void HandleRunStarted(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        runId = runId.Trim();
        clientId = clientId.Trim();

        var sessionId = GetOptionalString(data, "sessionId");
        var sessionKey = NormalizeSessionKey(sessionId);

        _runToClientId[runId] = clientId;
        _runToSessionKey[runId] = sessionKey;
        _completedRuns.Remove(runId);

        var session = GetSessionState(sessionKey);
        session.ActiveRunId = runId;
        session.HasCompletedBadge = false;
        session.HasWarningBadge = false;

        var message = GetOrCreateRunMessage(runId, sessionKey);
        message.IsTraceExpanded = true;
        message.RenderMarkdown = false;

        SessionRunStateChanged?.Invoke(this, sessionKey);
        SessionIndicatorChanged?.Invoke(this, sessionKey);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleSessionCreated(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "sessionId", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        runId = runId.Trim();
        sessionId = sessionId.Trim();

        EnsureRunSessionMapping(runId, sessionId);

        if (!IsLocalRun(runId))
        {
            return;
        }

        var newChat = GetSessionState(sessionId: null);
        var created = GetSessionState(sessionId);
        created.WorkingDirectoryOverride = newChat.WorkingDirectoryOverride;
        created.SandboxOverride = newChat.SandboxOverride;
        created.ApprovalPolicyOverride = newChat.ApprovalPolicyOverride;
        created.ModelOverride = newChat.ModelOverride;
        created.EffortOverride = newChat.EffortOverride;

        var workingDirectory = App.ConnectionService.WorkingDirectory;
        App.SessionState.CurrentSessionCwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();
        App.SessionState.CurrentSessionId = sessionId;
    }

    private void HandleTurnStarted(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "threadId", out var threadId) || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        runId = runId.Trim();
        threadId = threadId.Trim();

        EnsureRunSessionMapping(runId, threadId);
    }

    private void HandleChatMessage(JsonElement data)
    {
        if (!TryGetString(data, "role", out var role) || string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        var text = GetOptionalString(data, "text") ?? string.Empty;
        var images = GetOptionalStringArray(data, "images");

        role = role.Trim();
        runId = runId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);

        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            && _runToMessage.TryGetValue(runId, out var runMessage))
        {
            runMessage.Text = text;
            runMessage.RenderMarkdown = true;
            runMessage.IsTraceExpanded = runMessage.Trace.Any(entry => entry.IsDiff);
            AttachImages(runMessage, images);
            SessionContentUpdated?.Invoke(this, sessionKey);
            return;
        }

        var message = new ChatMessageViewModel(role, text, runId);
        AttachImages(message, images);
        GetSessionState(sessionKey).Messages.Add(message);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleChatMessageDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "delta", out var delta) || string.IsNullOrEmpty(delta))
        {
            return;
        }

        runId = runId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);
        if (string.Equals(message.Text, "思考中…", StringComparison.Ordinal)
            && !message.Trace.Any(entry => entry.IsDiff))
        {
            message.IsTraceExpanded = false;
        }
        message.AppendTextDelta(delta);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunCommand(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!TryGetString(data, "command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        runId = runId.Trim();
        itemId = itemId.Trim();
        command = command.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);

        var status = GetOptionalString(data, "status");
        if (string.IsNullOrWhiteSpace(status))
        {
            status = "completed";
        }

        var output = GetOptionalString(data, "output");
        var exitCode = TryGetInt32(data, "exitCode", out var parsedExitCode) ? parsedExitCode : (int?)null;
        message.UpsertCommandTrace(itemId, tool: null, command, status, exitCode, output);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleDiffUpdated(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!data.TryGetProperty("files", out var filesProp) || filesProp.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        runId = runId.Trim();
        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);
        var hasAnyDiff = false;

        foreach (var file in filesProp.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetString(file, "path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var diff = GetOptionalString(file, "diff");
            if (string.IsNullOrWhiteSpace(diff))
            {
                continue;
            }

            var added = TryGetInt32(file, "added", out var addedCount) ? addedCount : 0;
            var removed = TryGetInt32(file, "removed", out var removedCount) ? removedCount : 0;
            message.UpsertDiffTrace($"diff:{path.Trim()}", path.Trim(), diff, added, removed);
            hasAnyDiff = true;
        }

        if (hasAnyDiff)
        {
            message.IsTraceExpanded = true;
        }

        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleDiffSummary(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!data.TryGetProperty("files", out var filesProp) || filesProp.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        runId = runId.Trim();
        var files = new List<DiffFileSummary>(filesProp.GetArrayLength());
        foreach (var file in filesProp.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetString(file, "path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var added = TryGetInt32(file, "added", out var addedCount) ? addedCount : 0;
            var removed = TryGetInt32(file, "removed", out var removedCount) ? removedCount : 0;
            files.Add(new DiffFileSummary(path.Trim(), added, removed));
        }

        var totalAdded = TryGetInt32(data, "totalAdded", out var totalAddedCount) ? totalAddedCount : 0;
        var totalRemoved = TryGetInt32(data, "totalRemoved", out var totalRemovedCount) ? totalRemovedCount : 0;
        var summaryText = BuildDiffSummaryText(files, totalAdded, totalRemoved);
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            return;
        }
        _pendingDiffSummaries[runId] = new DiffSummarySnapshot(files, totalAdded, totalRemoved, summaryText);

        if (_completedRuns.Contains(runId))
        {
            var sessionKey = ResolveSessionKey(data, runId);
            AppendDiffSummaryIfReady(runId, sessionKey);
        }
    }

    private void HandleRunCommandOutputDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!TryGetString(data, "delta", out var delta) || string.IsNullOrEmpty(delta))
        {
            return;
        }

        runId = runId.Trim();
        itemId = itemId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);
        message.AppendCommandOutputDelta(itemId, delta);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunReasoning(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!TryGetString(data, "text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        runId = runId.Trim();
        itemId = itemId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);
        message.UpsertReasoningTrace(itemId, text);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunReasoningDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        if (!TryGetString(data, "itemId", out var itemId) || string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!TryGetString(data, "textDelta", out var textDelta) || string.IsNullOrEmpty(textDelta))
        {
            return;
        }

        runId = runId.Trim();
        itemId = itemId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var message = GetOrCreateRunMessage(runId, sessionKey);
        message.AppendReasoningDelta(itemId, textDelta);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunPlanUpdated(JsonElement data)
    {
        if (!TryGetString(data, "threadId", out var threadId) || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        threadId = threadId.Trim();

        if (TryGetString(data, "runId", out var runId) && !string.IsNullOrWhiteSpace(runId))
        {
            EnsureRunSessionMapping(runId.Trim(), threadId);
        }

        var explanation = GetOptionalString(data, "explanation");
        var updatedAtText = GetOptionalString(data, "updatedAt");
        var updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsedUpdatedAt) ? parsedUpdatedAt : (DateTimeOffset?)null;
        var turnId = GetOptionalString(data, "turnId");

        var steps = new List<TurnPlanStepViewModel>();
        if (data.TryGetProperty("plan", out var planProp) && planProp.ValueKind == JsonValueKind.Array)
        {
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

                TryGetString(entry, "status", out var status);
                steps.Add(new TurnPlanStepViewModel(step.Trim(), status?.Trim() ?? string.Empty));
            }
        }

        ApplyTurnPlan(threadId, explanation, steps, updatedAt, turnId);
    }

    private void ApplyTurnPlan(string sessionId, string? explanation, IReadOnlyList<TurnPlanStepViewModel> steps, DateTimeOffset? updatedAt, string? turnId)
    {
        var session = GetSessionState(sessionId);
        session.TurnPlanExplanation = string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim();
        session.TurnPlanUpdatedAt = updatedAt;
        session.TurnPlanTurnId = string.IsNullOrWhiteSpace(turnId) ? null : turnId.Trim();
        session.HasLoadedPlan = true;

        session.TurnPlanSteps.Clear();
        foreach (var step in steps)
        {
            session.TurnPlanSteps.Add(step);
        }

        SessionPlanUpdated?.Invoke(this, session.SessionKey);
        SessionContentUpdated?.Invoke(this, session.SessionKey);
    }

    private void HandleRunCompleted(JsonElement data, bool succeeded)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        runId = runId.Trim();

        var sessionKey = ResolveSessionKey(data, runId);
        var session = GetSessionState(sessionKey);

        if (string.Equals(session.ActiveRunId, runId, StringComparison.Ordinal))
        {
            session.ActiveRunId = null;
        }

        _completedRuns.Add(runId);
        AppendDiffSummaryIfReady(runId, sessionKey);

        if (_runToMessage.TryGetValue(runId, out var message))
        {
            message.IsTraceExpanded = false;
            message.RenderMarkdown = true;
        }

        if (!IsCurrentConversation(sessionKey))
        {
            if (succeeded)
            {
                session.HasCompletedBadge = true;
            }
            else
            {
                session.HasWarningBadge = true;
            }
        }

        SessionRunStateChanged?.Invoke(this, sessionKey);
        SessionIndicatorChanged?.Invoke(this, sessionKey);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunCanceled(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        runId = runId.Trim();
        var sessionKey = ResolveSessionKey(data, runId);
        var session = GetSessionState(sessionKey);

        if (string.Equals(session.ActiveRunId, runId, StringComparison.Ordinal))
        {
            session.ActiveRunId = null;
        }

        _completedRuns.Add(runId);
        AppendDiffSummaryIfReady(runId, sessionKey);

        if (!IsCurrentConversation(sessionKey))
        {
            session.HasWarningBadge = true;
        }

        SessionRunStateChanged?.Invoke(this, sessionKey);
        SessionIndicatorChanged?.Invoke(this, sessionKey);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void HandleRunRejected(JsonElement data)
    {
        var sessionId = GetOptionalString(data, "sessionId");
        var sessionKey = NormalizeSessionKey(sessionId) == NewChatSessionKey
            ? NormalizeSessionKey(App.SessionState.CurrentSessionId)
            : NormalizeSessionKey(sessionId);

        var session = GetSessionState(sessionKey);
        session.ActiveRunId = null;

        if (!IsCurrentConversation(sessionKey))
        {
            session.HasWarningBadge = true;
        }

        SessionRunStateChanged?.Invoke(this, sessionKey);
        SessionIndicatorChanged?.Invoke(this, sessionKey);
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private void AppendDiffSummaryIfReady(string runId, string sessionKey)
    {
        if (!_pendingDiffSummaries.TryGetValue(runId, out var summary))
        {
            return;
        }

        if (!_runToMessage.TryGetValue(runId, out var message))
        {
            return;
        }

        _pendingDiffSummaries.Remove(runId);

        var text = message.Text ?? string.Empty;
        if (string.Equals(text, "思考中…", StringComparison.Ordinal))
        {
            message.Text = summary.Text.TrimStart();
        }
        else
        {
            message.Text = string.Concat(text, summary.Text);
        }

        message.RenderMarkdown = true;
        SessionContentUpdated?.Invoke(this, sessionKey);
    }

    private static string BuildDiffSummaryText(IReadOnlyList<DiffFileSummary> files, int totalAdded, int totalRemoved)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("变更汇总：");

        foreach (var file in files)
        {
            builder.Append("- `");
            builder.Append(file.Path);
            builder.Append("` +");
            builder.Append(file.Added);
            builder.Append(" -");
            builder.Append(file.Removed);
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append("合计：+");
        builder.Append(totalAdded);
        builder.Append(" -");
        builder.Append(totalRemoved);
        return builder.ToString();
    }

    private sealed record DiffFileSummary(string Path, int Added, int Removed);

    private sealed record DiffSummarySnapshot(
        IReadOnlyList<DiffFileSummary> Files,
        int TotalAdded,
        int TotalRemoved,
        string Text);

    private void EnsureRunSessionMapping(string runId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var newSessionKey = NormalizeSessionKey(sessionId);
        if (_runToSessionKey.TryGetValue(runId, out var oldSessionKey)
            && string.Equals(oldSessionKey, newSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        oldSessionKey = _runToSessionKey.TryGetValue(runId, out var existing) ? existing : NewChatSessionKey;
        _runToSessionKey[runId] = newSessionKey;

        if (!string.Equals(oldSessionKey, newSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            MoveRunMessages(runId, oldSessionKey, newSessionKey);
        }
    }

    private void MoveRunMessages(string runId, string fromSessionKey, string toSessionKey)
    {
        var from = GetSessionState(fromSessionKey);
        var to = GetSessionState(toSessionKey);

        var messagesToMove = from.Messages
            .Where(msg => !string.IsNullOrWhiteSpace(msg.RunId) && string.Equals(msg.RunId, runId, StringComparison.Ordinal))
            .ToList();

        if (messagesToMove.Count == 0)
        {
            if (string.Equals(from.ActiveRunId, runId, StringComparison.Ordinal))
            {
                from.ActiveRunId = null;
                to.ActiveRunId = runId;
                SessionRunStateChanged?.Invoke(this, fromSessionKey);
                SessionRunStateChanged?.Invoke(this, toSessionKey);
                SessionIndicatorChanged?.Invoke(this, fromSessionKey);
                SessionIndicatorChanged?.Invoke(this, toSessionKey);
            }

            return;
        }

        foreach (var message in messagesToMove)
        {
            _ = from.Messages.Remove(message);
            to.Messages.Add(message);
        }

        if (string.Equals(from.ActiveRunId, runId, StringComparison.Ordinal))
        {
            from.ActiveRunId = null;
            to.ActiveRunId = runId;
        }

        SessionRunStateChanged?.Invoke(this, fromSessionKey);
        SessionRunStateChanged?.Invoke(this, toSessionKey);
        SessionIndicatorChanged?.Invoke(this, fromSessionKey);
        SessionIndicatorChanged?.Invoke(this, toSessionKey);
        SessionContentUpdated?.Invoke(this, toSessionKey);
    }

    private ChatMessageViewModel GetOrCreateRunMessage(string runId, string sessionKey)
    {
        if (_runToMessage.TryGetValue(runId, out var message))
        {
            return message;
        }

        message = new ChatMessageViewModel("assistant", "思考中…", runId, renderMarkdown: false);
        message.IsTraceExpanded = true;
        _runToMessage[runId] = message;

        GetSessionState(sessionKey).Messages.Add(message);
        return message;
    }

    private string ResolveSessionKey(JsonElement data, string runId)
    {
        var sessionId = GetOptionalString(data, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            EnsureRunSessionMapping(runId, sessionId);
        }

        if (_runToSessionKey.TryGetValue(runId, out var existing))
        {
            return existing;
        }

        return NormalizeSessionKey(sessionId);
    }

    private bool IsLocalRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(_localClientId))
        {
            return false;
        }

        if (!_runToClientId.TryGetValue(runId, out var clientId))
        {
            return false;
        }

        return string.Equals(_localClientId, clientId, StringComparison.Ordinal);
    }

    private void OnCurrentSessionChanged()
    {
        var sessionId = App.SessionState.CurrentSessionId;
        if (!_chatPageActive || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var session = GetSessionState(sessionId);
        if (!session.HasCompletedBadge && !session.HasWarningBadge)
        {
            return;
        }

        session.HasCompletedBadge = false;
        session.HasWarningBadge = false;
        SessionIndicatorChanged?.Invoke(this, session.SessionKey);
    }

    private bool IsCurrentConversation(string sessionKey)
    {
        if (!_chatPageActive)
        {
            return false;
        }

        var currentSessionId = App.SessionState.CurrentSessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return string.Equals(sessionKey, NewChatSessionKey, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(sessionKey, currentSessionId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSessionKey(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? NewChatSessionKey : sessionId.Trim();

    private void AttachImages(ChatMessageViewModel message, IReadOnlyList<string>? imageDataUrls)
    {
        if (imageDataUrls is null || imageDataUrls.Count == 0)
        {
            return;
        }

        foreach (var url in imageDataUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var vm = new ChatImageViewModel(url);
            message.Images.Add(vm);
            _ = vm.LoadAsync();
        }
    }

    private static IReadOnlyList<string>? GetOptionalStringArray(JsonElement data, string propertyName)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>(capacity: 4);
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    private static string? GetOptionalString(JsonElement data, string propertyName) =>
        TryGetString(data, propertyName, out var value) ? value : null;

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

    private static bool TryGetInt32(JsonElement data, string propertyName, out int value)
    {
        value = 0;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetInt32(out value);
    }
}
