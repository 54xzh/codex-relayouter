// ChatPage：聊天页面，使用全局 ConnectionService。
using codex_bridge.Bridge;
using codex_bridge.Models;
using codex_bridge.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace codex_bridge.Pages;

public sealed partial class ChatPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxPendingImages = 4;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const double AutoScrollBottomTolerance = 24;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, ChatMessageViewModel> _runToMessage = new();
    private string? _historyLoadedForSessionId;
    private int _autoConnectAttempted;
    private ScrollViewer? _messagesScrollViewer;
    private bool _scrollToBottomPending;
    private bool _forceScrollToBottomOnNextContentUpdate;
    private bool _isRunning;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<ChatImageViewModel> PendingImages { get; } = new();

    public ChatPage()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        App.ConnectionService.EnvelopeReceived += ConnectionService_EnvelopeReceived;
        App.ConnectionService.ConnectionStateChanged += ConnectionService_StateChanged;
        App.ConnectionService.ConnectionClosed += ConnectionService_ConnectionClosed;

        PendingImages.CollectionChanged += PendingImages_CollectionChanged;

        Loaded += ChatPage_Loaded;
    }

    private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateConnectionUI();
        ApplySessionStateToUi();
        ApplyConnectionSettingsToUi();
        UpdatePendingImagesUi();
        UpdateActionButtonsVisibility();
        EnsureMessagesScrollViewer();
        await EnsureBackendAndConnectAsync();
        await LoadSessionHistoryIfNeededAsync();
    }

    private async Task EnsureBackendAndConnectAsync()
    {
        if (Interlocked.Exchange(ref _autoConnectAttempted, 1) == 1)
        {
            return;
        }

        try
        {
            await App.BackendServer.EnsureStartedAsync();

            var wsUri = App.BackendServer.WebSocketUri;
            if (wsUri is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(App.ConnectionService.ServerUrl))
            {
                App.ConnectionService.ServerUrl = wsUri.ToString();
            }

            if (!App.ConnectionService.IsConnected)
            {
                ConnectionStatusText.Text = "连接中…";
                await App.ConnectionService.ConnectAsync(wsUri, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            SetSessionStatus($"自动连接失败: {ex.Message}");
        }
        finally
        {
            UpdateConnectionUI();
        }
    }

    private void ApplyConnectionSettingsToUi()
    {
        ApplyWorkingDirectoryToUi();
        SandboxText.Text = string.IsNullOrEmpty(App.ConnectionService.Sandbox) ? "默认" : App.ConnectionService.Sandbox;
        ModelText.Text = string.IsNullOrEmpty(App.ConnectionService.Model) ? "默认" : App.ConnectionService.Model;
        ThinkingText.Text = string.IsNullOrEmpty(App.ConnectionService.Effort) ? "默认" : App.ConnectionService.Effort;
        ApprovalText.Text = string.IsNullOrEmpty(App.ConnectionService.ApprovalPolicy) ? "默认" : App.ConnectionService.ApprovalPolicy;
    }

    private void ApplyWorkingDirectoryToUi()
    {
        var service = App.ConnectionService;
        var workingDirectory = service.WorkingDirectory;
        WorkspaceText.Text = GetDirectoryNameOrFallback(workingDirectory, emptyLabel: "未选择");

        var hasWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory);
        var workingDirectoryExists = hasWorkingDirectory && Directory.Exists(workingDirectory);

        WorkspaceOpenInExplorerMenuItem.IsEnabled = workingDirectoryExists;

        ApplyRecentWorkingDirectoriesToMenu(service.RecentWorkingDirectories, workingDirectory);
    }

    private void ApplyRecentWorkingDirectoriesToMenu(IReadOnlyList<string> recentWorkingDirectories, string? currentWorkingDirectory)
    {
        var items = new[]
        {
            RecentWorkspace1MenuItem,
            RecentWorkspace2MenuItem,
            RecentWorkspace3MenuItem,
            RecentWorkspace4MenuItem,
            RecentWorkspace5MenuItem,
        };

        var count = 0;
        foreach (var entry in recentWorkingDirectories)
        {
            if (count >= items.Length)
            {
                break;
            }

            var item = items[count];
            item.Text = entry;
            item.Tag = entry;
            item.Visibility = Visibility.Visible;
            item.IsEnabled = Directory.Exists(entry)
                && !string.Equals(entry, currentWorkingDirectory, StringComparison.OrdinalIgnoreCase);

            count++;
        }

        for (var i = count; i < items.Length; i++)
        {
            items[i].Text = string.Empty;
            items[i].Tag = null;
            items[i].Visibility = Visibility.Collapsed;
        }

        var hasAny = recentWorkingDirectories.Count > 0;
        WorkspaceRecentSeparator.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceRecentHeaderMenuItem.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetDirectoryNameOrFallback(string? path, string emptyLabel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return emptyLabel;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to settings page
        if (Frame?.Parent is NavigationView navView)
        {
            foreach (var item in navView.MenuItems)
            {
                if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == "settings")
                {
                    navView.SelectedItem = navItem;
                    break;
                }
            }
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }

    private async void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PendingImages.Count >= MaxPendingImages)
            {
                SetSessionStatus($"最多添加 {MaxPendingImages} 张图片");
                return;
            }

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            var window = App.MainWindow;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                if (PendingImages.Count >= MaxPendingImages)
                {
                    break;
                }

                var dataUrl = await TryCreateImageDataUrlAsync(file.Path);
                if (string.IsNullOrWhiteSpace(dataUrl))
                {
                    continue;
                }

                AddPendingImage(dataUrl);
            }

            UpdatePendingImagesUi();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"选择图片失败: {ex.Message}");
        }
    }

    private void AddPendingImage(string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return;
        }

        var vm = new ChatImageViewModel(dataUrl);
        PendingImages.Add(vm);
        _ = vm.LoadAsync();
    }

    private void RemovePendingImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string dataUrl)
        {
            return;
        }

        for (var i = PendingImages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(PendingImages[i].DataUrl, dataUrl, StringComparison.Ordinal))
            {
                PendingImages.RemoveAt(i);
                break;
            }
        }

        UpdatePendingImagesUi();
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.ConnectionService.SendCommandAsync("run.cancel", new { }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetSessionStatus($"取消失败: {ex.Message}");
        }
    }

    private async Task SendPromptAsync()
    {
        var prompt = PromptTextBox.Text?.Trim() ?? string.Empty;

        string[]? images = null;
        if (PendingImages.Count > 0)
        {
            var list = new List<string>(PendingImages.Count);
            foreach (var img in PendingImages)
            {
                if (!string.IsNullOrWhiteSpace(img.DataUrl))
                {
                    list.Add(img.DataUrl);
                }
            }

            images = list.Count == 0 ? null : list.ToArray();
        }

        if (string.IsNullOrWhiteSpace(prompt) && images is null)
        {
            return;
        }

        PromptTextBox.Text = string.Empty;
        _forceScrollToBottomOnNextContentUpdate = true;

        try
        {
            var service = App.ConnectionService;

            await service.SendCommandAsync(
                "chat.send",
                new
                {
                    prompt,
                    images,
                    sessionId = App.SessionState.CurrentSessionId,
                    workingDirectory = service.WorkingDirectory,
                    model = service.Model,
                    sandbox = service.Sandbox,
                    approvalPolicy = service.ApprovalPolicy,
                    effort = service.Effort,
                    skipGitRepoCheck = service.SkipGitRepoCheck,
                },
                CancellationToken.None);

            PendingImages.Clear();
            UpdatePendingImagesUi();
        }
        catch (Exception ex)
        {
            _forceScrollToBottomOnNextContentUpdate = false;
            SetSessionStatus($"发送失败: {ex.Message}");
        }
    }

    private async Task LoadSessionHistoryIfNeededAsync()
    {
        var sessionId = App.SessionState.CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (string.Equals(_historyLoadedForSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var baseUri = App.BackendServer.HttpBaseUri;
        if (baseUri is null)
        {
            return;
        }

        try
        {
            SetSessionStatus("加载会话历史…");

            var path = $"api/v1/sessions/{Uri.EscapeDataString(sessionId)}/messages?limit=500";
            var uri = new Uri(baseUri, path);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var token = App.ConnectionService.BearerToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _httpClient.SendAsync(request, CancellationToken.None);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                SetSessionStatus($"会话不存在: {sessionId}");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var items = JsonSerializer.Deserialize<SessionMessage[]>(json, JsonOptions) ?? Array.Empty<SessionMessage>();

            Messages.Clear();
            _runToMessage.Clear();

            var traceIndex = 0;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Role)
                    || (string.IsNullOrWhiteSpace(item.Text) && (item.Images is null || item.Images.Length == 0)))
                {
                    continue;
                }

                var message = new ChatMessageViewModel(item.Role, item.Text ?? string.Empty);
                AttachImages(message, item.Images);

                if (item.Trace is not null && item.Trace.Length > 0)
                {
                    foreach (var trace in item.Trace)
                    {
                        traceIndex++;
                        var id = $"hist_{traceIndex}";

                        if (string.Equals(trace.Kind, "reasoning", StringComparison.OrdinalIgnoreCase))
                        {
                            message.Trace.Add(TraceEntryViewModel.CreateReasoning(id, trace.Title, trace.Text));
                            continue;
                        }

                        if (string.Equals(trace.Kind, "command", StringComparison.OrdinalIgnoreCase))
                        {
                            var command = string.IsNullOrWhiteSpace(trace.Command) ? (trace.Tool ?? "command") : trace.Command;
                            message.UpsertCommandTrace(
                                id,
                                trace.Tool,
                                command ?? "command",
                                trace.Status,
                                trace.ExitCode,
                                trace.Output);
                        }
                    }
                }

                Messages.Add(message);
            }

            _historyLoadedForSessionId = sessionId;
            SetSessionStatus($"会话: {sessionId}（{Messages.Count} 条消息）");
            await ScrollMessagesToBottomAfterHistoryLoadAsync();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"加载历史失败: {ex.Message}");
        }
    }

    private async Task ScrollMessagesToBottomAfterHistoryLoadAsync()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        await Task.Yield();

        await RunOnUiThreadAsync(() =>
        {
            if (Messages.Count == 0)
            {
                return;
            }

            MessagesListView.UpdateLayout();
            MessagesListView.ScrollIntoView(Messages[^1]);
        });
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("无法调度到 UI 线程。"));
        }

        return tcs.Task;
    }

    private void ConnectionService_EnvelopeReceived(object? sender, BridgeEnvelope envelope)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                HandleEnvelopeOnUiThread(envelope);
            }
            catch (Exception ex)
            {
                SetSessionStatus($"处理消息失败: {ex.Message}");
            }
        });
    }

    private void ConnectionService_StateChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(UpdateConnectionUI);
    }

    private void ConnectionService_ConnectionClosed(object? sender, string message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionUI();
            SetSessionStatus($"连接已关闭: {message}");
        });
    }

    private void HandleEnvelopeOnUiThread(BridgeEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureMessagesScrollViewer();
        var wasAtBottom = IsMessagesScrollAtBottom();
        var contentUpdated = false;

        switch (envelope.Name)
        {
            case "bridge.connected":
                if (TryGetString(envelope.Data, "clientId", out var clientId))
                {
                    SetSessionStatus($"已连接: {clientId}");
                }
                break;
            case "chat.message":
                HandleChatMessage(envelope.Data);
                contentUpdated = true;
                break;
            case "chat.message.delta":
                HandleChatMessageDelta(envelope.Data);
                contentUpdated = true;
                break;
            case "run.started":
                HandleRunStarted(envelope.Data);
                contentUpdated = true;
                break;
            case "session.created":
                HandleSessionCreated(envelope.Data);
                break;
            case "turn.started":
                break;
            case "codex.line":
                HandleCodexLine(envelope.Data);
                contentUpdated = true;
                break;
            case "run.command":
                HandleRunCommand(envelope.Data);
                contentUpdated = true;
                break;
            case "run.command.outputDelta":
                HandleRunCommandOutputDelta(envelope.Data);
                contentUpdated = true;
                break;
            case "run.reasoning":
                HandleRunReasoning(envelope.Data);
                contentUpdated = true;
                break;
            case "run.reasoning.delta":
                HandleRunReasoningDelta(envelope.Data);
                contentUpdated = true;
                break;
            case "approval.requested":
                _ = HandleApprovalRequestedAsync(envelope.Data);
                break;
            case "approval.responded":
                break;
            case "run.completed":
                HandleRunCompleted(envelope.Data);
                contentUpdated = true;
                break;
            case "run.canceled":
                HandleRunCanceled(envelope.Data);
                break;
            case "run.failed":
                HandleRunFailed(envelope.Data);
                contentUpdated = true;
                break;
            case "run.rejected":
                HandleRunRejected(envelope.Data);
                break;
        }

        if (contentUpdated && (_forceScrollToBottomOnNextContentUpdate || wasAtBottom))
        {
            _forceScrollToBottomOnNextContentUpdate = false;
            RequestScrollMessagesToBottom();
        }
    }

    private void EnsureMessagesScrollViewer()
    {
        if (_messagesScrollViewer is not null)
        {
            return;
        }

        MessagesListView.UpdateLayout();
        _messagesScrollViewer = FindDescendant<ScrollViewer>(MessagesListView);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private bool IsMessagesScrollAtBottom()
    {
        if (_messagesScrollViewer is null)
        {
            return true;
        }

        if (_messagesScrollViewer.ScrollableHeight <= 0)
        {
            return true;
        }

        return _messagesScrollViewer.VerticalOffset >= _messagesScrollViewer.ScrollableHeight - AutoScrollBottomTolerance;
    }

    private void RequestScrollMessagesToBottom()
    {
        if (_scrollToBottomPending || Messages.Count == 0)
        {
            return;
        }

        _scrollToBottomPending = true;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollMessagesToBottom);
    }

    private void ScrollMessagesToBottom()
    {
        _scrollToBottomPending = false;

        if (Messages.Count == 0)
        {
            return;
        }

        MessagesListView.UpdateLayout();

        if (_messagesScrollViewer is null)
        {
            EnsureMessagesScrollViewer();
        }

        if (_messagesScrollViewer is null)
        {
            MessagesListView.ScrollIntoView(Messages[^1]);
            return;
        }

        _messagesScrollViewer.ChangeView(null, _messagesScrollViewer.ScrollableHeight, null, true);
    }

    private void HandleChatMessage(JsonElement data)
    {
        if (!TryGetString(data, "role", out var role) || !TryGetString(data, "text", out var text))
        {
            return;
        }

        var runId = GetOptionalString(data, "runId");
        var images = GetOptionalStringArray(data, "images");

        if (!string.IsNullOrWhiteSpace(runId)
            && string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            && _runToMessage.TryGetValue(runId, out var runMessage))
        {
            runMessage.Text = text;
            AttachImages(runMessage, images);
            runMessage.IsTraceExpanded = false;
            return;
        }

        var message = new ChatMessageViewModel(role, text, runId);
        Messages.Add(message);
        AttachImages(message, images);
    }

    private void HandleChatMessageDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || !TryGetString(data, "delta", out var delta))
        {
            return;
        }

        var message = GetOrCreateRunMessage(runId);
        if (string.Equals(message.Text, "思考中…", StringComparison.Ordinal))
        {
            message.IsTraceExpanded = false;
        }
        message.AppendTextDelta(delta);
    }

    private void HandleRunStarted(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId))
        {
            return;
        }

        var message = new ChatMessageViewModel("assistant", "思考中…", runId);
        message.IsTraceExpanded = true;
        _runToMessage[runId] = message;
        Messages.Add(message);
        SetSessionStatus($"运行中: {runId}");

        _isRunning = true;
        UpdateActionButtonsVisibility();
    }

    private void HandleSessionCreated(JsonElement data)
    {
        if (!TryGetString(data, "sessionId", out var sessionId))
        {
            return;
        }

        var workingDirectory = App.ConnectionService.WorkingDirectory;
        App.SessionState.CurrentSessionCwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        App.SessionState.CurrentSessionId = sessionId;
        SetSessionStatus($"会话: {sessionId}");
    }

    private void HandleCodexLine(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId) || !TryGetString(data, "payload", out var payload))
        {
            return;
        }

        var message = GetOrCreateRunMessage(runId);
        message.AppendLine(payload);
    }

    private void HandleRunCommand(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId)
            || !TryGetString(data, "itemId", out var itemId)
            || !TryGetString(data, "command", out var command))
        {
            return;
        }

        var status = GetOptionalString(data, "status");
        if (string.IsNullOrWhiteSpace(status))
        {
            status = "completed";
        }

        var output = GetOptionalString(data, "output");
        var exitCode = TryGetInt32(data, "exitCode", out var parsedExitCode) ? parsedExitCode : (int?)null;

        var message = GetOrCreateRunMessage(runId);
        message.UpsertCommandTrace(itemId, tool: null, command, status, exitCode, output);
    }

    private void HandleRunCommandOutputDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId)
            || !TryGetString(data, "itemId", out var itemId)
            || !TryGetString(data, "delta", out var delta))
        {
            return;
        }

        var message = GetOrCreateRunMessage(runId);
        message.AppendCommandOutputDelta(itemId, delta);
    }

    private void HandleRunReasoning(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId)
            || !TryGetString(data, "itemId", out var itemId)
            || !TryGetString(data, "text", out var text))
        {
            return;
        }

        var message = GetOrCreateRunMessage(runId);
        message.UpsertReasoningTrace(itemId, text);
    }

    private void HandleRunReasoningDelta(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId)
            || !TryGetString(data, "itemId", out var itemId)
            || !TryGetString(data, "textDelta", out var textDelta))
        {
            return;
        }

        var message = GetOrCreateRunMessage(runId);
        message.AppendReasoningDelta(itemId, textDelta);
    }

    private async Task HandleApprovalRequestedAsync(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId)
            || !TryGetString(data, "requestId", out var requestId)
            || !TryGetString(data, "kind", out var kind))
        {
            return;
        }

        var reason = GetOptionalString(data, "reason");
        var itemId = GetOptionalString(data, "itemId");

        var acceptForSessionCheckBox = new CheckBox
        {
            Content = "本会话内自动允许同类操作",
            IsChecked = false,
        };

        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            body.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        }

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            body.Children.Add(new TextBlock { Opacity = 0.7, Text = $"itemId: {itemId}" });
        }

        body.Children.Add(acceptForSessionCheckBox);

        var title = string.Equals(kind, "commandExecution", StringComparison.OrdinalIgnoreCase)
            ? "需要批准：执行命令"
            : "需要批准：修改文件";

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = "允许",
            SecondaryButtonText = "拒绝",
            CloseButtonText = "取消任务",
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();

        var decision = result switch
        {
            ContentDialogResult.Primary => acceptForSessionCheckBox.IsChecked == true ? "acceptForSession" : "accept",
            ContentDialogResult.Secondary => "decline",
            _ => "cancel",
        };

        try
        {
            await App.ConnectionService.SendCommandAsync(
                "approval.respond",
                new { runId, requestId, decision },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetSessionStatus($"发送审批失败: {ex.Message}");
        }
    }

    private ChatMessageViewModel GetOrCreateRunMessage(string runId)
    {
        if (_runToMessage.TryGetValue(runId, out var message))
        {
            return message;
        }

        message = new ChatMessageViewModel("assistant", "思考中…", runId);
        message.IsTraceExpanded = true;
        _runToMessage[runId] = message;
        Messages.Add(message);
        return message;
    }

    private void ApplySessionStateToUi()
    {
        var sessionId = App.SessionState.CurrentSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            SetSessionStatus($"会话: {sessionId}");
        }
    }

    private void HandleRunCompleted(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId))
        {
            return;
        }

        var exitCodeInfo = TryGetInt32(data, "exitCode", out var exitCode) ? $"(exitCode={exitCode})" : string.Empty;
        var prefix = string.IsNullOrWhiteSpace(exitCodeInfo) ? "完成" : $"完成{exitCodeInfo}";
        SetSessionStatus($"{prefix}: {runId}");

        if (_runToMessage.TryGetValue(runId, out var message))
        {
            message.IsTraceExpanded = false;
        }

        _isRunning = false;
        UpdateActionButtonsVisibility();
    }

    private void HandleRunCanceled(JsonElement data)
    {
        if (TryGetString(data, "runId", out var runId))
        {
            SetSessionStatus($"已取消: {runId}");
        }

        _isRunning = false;
        UpdateActionButtonsVisibility();
    }

    private void HandleRunFailed(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId))
        {
            return;
        }

        var hasExitCode = TryGetInt32(data, "exitCode", out var exitCode);
        var exitCodeInfo = hasExitCode ? $"(exitCode={exitCode})" : string.Empty;

        var hasMessage = TryGetString(data, "message", out var message) && !string.IsNullOrWhiteSpace(message);
        var statusMessage = hasMessage ? message.Trim() : "未知错误";
        var prefix = string.IsNullOrWhiteSpace(exitCodeInfo) ? "失败" : $"失败{exitCodeInfo}";
        SetSessionStatus($"{prefix}: {runId} {statusMessage}".Trim());

        if (!hasMessage)
        {
            return;
        }

        if (!_runToMessage.TryGetValue(runId, out var runMessage))
        {
            runMessage = new ChatMessageViewModel("assistant", string.Empty, runId);
            _runToMessage[runId] = runMessage;
            Messages.Add(runMessage);
        }

        if (string.IsNullOrWhiteSpace(runMessage.Text))
        {
            runMessage.Text = message.Trim();
        }

        _isRunning = false;
        UpdateActionButtonsVisibility();
    }

    private void HandleRunRejected(JsonElement data)
    {
        if (TryGetString(data, "reason", out var reason))
        {
            SetSessionStatus($"被拒绝: {reason}");
        }

        _isRunning = false;
        UpdateActionButtonsVisibility();
    }

    private void UpdateActionButtonsVisibility()
    {
        SendButton.Visibility = _isRunning ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateConnectionUI()
    {
        var isConnected = App.ConnectionService.IsConnected;

        ConnectionStatusText.Text = isConnected ? "已连接" : "未连接";
        StatusIndicator.Fill = isConnected
            ? new SolidColorBrush(Colors.LimeGreen)
            : Application.Current.Resources["SystemFillColorCautionBrush"] as Brush;
    }

    private void SetSessionStatus(string text)
    {
        SessionStatusText.Text = text;
    }

    private void PendingImages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdatePendingImagesUi();
    }

    private void UpdatePendingImagesUi()
    {
        if (PendingImagesScrollViewer is null)
        {
            return;
        }

        PendingImagesScrollViewer.Visibility = PendingImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private static bool IsSupportedImageExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }

    private static bool TryNormalizeImageDataUrl(string? text, out string dataUrl)
    {
        dataUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex < 0)
        {
            return false;
        }

        var meta = trimmed.Substring(0, commaIndex);
        var semicolonIndex = meta.IndexOf(';');
        if (semicolonIndex < 0)
        {
            return false;
        }

        var mimeType = meta.Substring("data:".Length, semicolonIndex - "data:".Length);
        if (!IsSupportedImageMimeType(mimeType))
        {
            return false;
        }

        if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = trimmed.Substring(commaIndex + 1).Trim();
        if (payload.Length == 0)
        {
            return false;
        }

        if (EstimateDecodedBytesFromBase64(payload) > MaxImageBytes)
        {
            return false;
        }

        dataUrl = trimmed;
        return true;
    }

    private static bool IsSupportedImageMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        return mimeType.Trim().ToLowerInvariant() is "image/png" or "image/jpeg" or "image/webp" or "image/gif";
    }

    private static long EstimateDecodedBytesFromBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        var padding = 0;
        if (base64.EndsWith("==", StringComparison.Ordinal))
        {
            padding = 2;
        }
        else if (base64.EndsWith("=", StringComparison.Ordinal))
        {
            padding = 1;
        }

        var length = (long)base64.Length;
        var decodedLength = (length * 3 / 4) - padding;
        return decodedLength < 0 ? 0 : decodedLength;
    }

    private static Task<string?> TryCreateImageDataUrlAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".bmp")
        {
            return TryTranscodeFileToPngDataUrlAsync(path);
        }

        var mimeType = ext switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };

        if (mimeType is null)
        {
            return Task.FromResult<string?>(null);
        }

        return ReadAndEncodeAsync(path, mimeType);
    }

    private static async Task<string?> TryCreateImageDataUrlAsync(RandomAccessStreamReference? bitmap)
    {
        if (bitmap is null)
        {
            return null;
        }

        IRandomAccessStreamWithContentType stream;
        try
        {
            stream = await bitmap.OpenReadAsync();
        }
        catch
        {
            return null;
        }

        using (stream)
        {
            return await TryTranscodeToPngDataUrlAsync(stream);
        }
    }

    private static async Task<string?> ReadAndEncodeAsync(string path, string mimeType)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch
        {
            return null;
        }

        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            return null;
        }

        var base64 = Convert.ToBase64String(bytes);
        return $"data:{mimeType};base64,{base64}";
    }

    private static async Task<string?> TryTranscodeFileToPngDataUrlAsync(string path)
    {
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path);
        }
        catch
        {
            return null;
        }

        try
        {
            using var stream = await file.OpenReadAsync();
            return await TryTranscodeToPngDataUrlAsync(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryTranscodeToPngDataUrlAsync(IRandomAccessStream inputStream)
    {
        SoftwareBitmap? softwareBitmap = null;

        try
        {
            inputStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            outputStream.Seek(0);
            using var output = outputStream.AsStreamForRead();
            using var memory = new MemoryStream();
            await output.CopyToAsync(memory);

            var bytes = memory.ToArray();
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return null;
            }

            var base64 = Convert.ToBase64String(bytes);
            return $"data:image/png;base64,{base64}";
        }
        catch
        {
            return null;
        }
        finally
        {
            softwareBitmap?.Dispose();
        }
    }

    private static string? GetOptionalString(JsonElement data, string propertyName) =>
        TryGetString(data, propertyName, out var value) ? value : null;

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

    private async void PromptTextBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            if (PendingImages.Count >= MaxPendingImages)
            {
                SetSessionStatus($"最多添加 {MaxPendingImages} 张图片");
                return;
            }

            var content = Clipboard.GetContent();
            if (content is null)
            {
                return;
            }

            var shouldHandle = false;
            var addedAny = false;

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                shouldHandle = true;

                var bitmap = await content.GetBitmapAsync();
                var dataUrl = await TryCreateImageDataUrlAsync(bitmap);
                if (!string.IsNullOrWhiteSpace(dataUrl))
                {
                    AddPendingImage(dataUrl);
                    addedAny = true;
                }
            }
            else if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (PendingImages.Count >= MaxPendingImages)
                    {
                        shouldHandle = true;
                        break;
                    }

                    if (item is not StorageFile file)
                    {
                        continue;
                    }

                    if (!IsSupportedImageExtension(file.Path))
                    {
                        continue;
                    }

                    shouldHandle = true;
                    var dataUrl = await TryCreateImageDataUrlAsync(file.Path);
                    if (string.IsNullOrWhiteSpace(dataUrl))
                    {
                        continue;
                    }

                    AddPendingImage(dataUrl);
                    addedAny = true;
                }
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (TryNormalizeImageDataUrl(text, out var dataUrl))
                {
                    shouldHandle = true;
                    AddPendingImage(dataUrl);
                    addedAny = true;
                }
            }

            if (!shouldHandle)
            {
                return;
            }

            e.Handled = true;
            if (!addedAny && PendingImages.Count >= MaxPendingImages)
            {
                SetSessionStatus($"最多添加 {MaxPendingImages} 张图片");
            }
            else if (!addedAny)
            {
                SetSessionStatus("未检测到可用图片（可能格式不支持或图片过大）");
            }

            UpdatePendingImagesUi();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"粘贴图片失败: {ex.Message}");
        }
    }

    private async void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var shiftDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftDown)
        {
            return;
        }

        e.Handled = true;
        await SendPromptAsync();
    }

    // 设置栏事件处理方法
    private void WorkspaceOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var workingDirectory = App.ConnectionService.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            SetSessionStatus("未选择工作目录");
            ApplyWorkingDirectoryToUi();
            return;
        }

        if (!Directory.Exists(workingDirectory))
        {
            SetSessionStatus($"目录不存在: {workingDirectory}");
            ApplyWorkingDirectoryToUi();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{workingDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetSessionStatus($"打开目录失败: {ex.Message}");
        }
    }

    private void RecentWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string workingDirectory)
        {
            return;
        }

        if (!Directory.Exists(workingDirectory))
        {
            SetSessionStatus($"目录不存在: {workingDirectory}");
            ApplyWorkingDirectoryToUi();
            return;
        }

        App.ConnectionService.WorkingDirectory = workingDirectory;
        ApplyWorkingDirectoryToUi();
    }

    private async void WorkspaceReselect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            var window = App.MainWindow;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            App.ConnectionService.WorkingDirectory = folder.Path;
            ApplyWorkingDirectoryToUi();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"选择目录失败: {ex.Message}");
        }
    }

    private void SandboxOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            SandboxText.Text = string.IsNullOrEmpty(value) ? "默认" : value;
            App.ConnectionService.Sandbox = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private void ApprovalOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            ApprovalText.Text = string.IsNullOrEmpty(value) ? "默认" : value;
            App.ConnectionService.ApprovalPolicy = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private void ModelOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            ModelText.Text = string.IsNullOrEmpty(value) ? "默认" : value;
            App.ConnectionService.Model = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private void ThinkingOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            ThinkingText.Text = string.IsNullOrEmpty(value) ? "默认" : value;
            App.ConnectionService.Effort = string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
