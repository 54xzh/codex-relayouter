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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace codex_bridge.Pages;

public sealed partial class ChatPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxPendingImages = 4;
    private const int MaxImageBytes = 10 * 1024 * 1024;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, ChatMessageViewModel> _runToMessage = new();
    private string? _historyLoadedForSessionId;
    private int _autoConnectAttempted;

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

                var vm = new ChatImageViewModel(dataUrl);
                PendingImages.Add(vm);
                _ = vm.LoadAsync();
            }

            UpdatePendingImagesUi();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"选择图片失败: {ex.Message}");
        }
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
        }
        catch (Exception ex)
        {
            SetSessionStatus($"加载历史失败: {ex.Message}");
        }
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
                break;
            case "chat.message.delta":
                HandleChatMessageDelta(envelope.Data);
                break;
            case "run.started":
                HandleRunStarted(envelope.Data);
                break;
            case "session.created":
                HandleSessionCreated(envelope.Data);
                break;
            case "turn.started":
                break;
            case "codex.line":
                HandleCodexLine(envelope.Data);
                break;
            case "run.command":
                HandleRunCommand(envelope.Data);
                break;
            case "run.command.outputDelta":
                HandleRunCommandOutputDelta(envelope.Data);
                break;
            case "run.reasoning":
                HandleRunReasoning(envelope.Data);
                break;
            case "run.reasoning.delta":
                HandleRunReasoningDelta(envelope.Data);
                break;
            case "approval.requested":
                _ = HandleApprovalRequestedAsync(envelope.Data);
                break;
            case "approval.responded":
                break;
            case "run.completed":
                HandleRunCompleted(envelope.Data);
                break;
            case "run.canceled":
                HandleRunCanceled(envelope.Data);
                break;
            case "run.failed":
                HandleRunFailed(envelope.Data);
                break;
            case "run.rejected":
                HandleRunRejected(envelope.Data);
                break;
        }

        if (Messages.Count > 0)
        {
            MessagesListView.ScrollIntoView(Messages[^1]);
        }
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
        message.AppendTextDelta(delta);
    }

    private void HandleRunStarted(JsonElement data)
    {
        if (!TryGetString(data, "runId", out var runId))
        {
            return;
        }

        var message = new ChatMessageViewModel("assistant", "思考中…", runId);
        _runToMessage[runId] = message;
        Messages.Add(message);
        SetSessionStatus($"运行中: {runId}");
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
    }

    private void HandleRunCanceled(JsonElement data)
    {
        if (TryGetString(data, "runId", out var runId))
        {
            SetSessionStatus($"已取消: {runId}");
        }
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
    }

    private void HandleRunRejected(JsonElement data)
    {
        if (TryGetString(data, "reason", out var reason))
        {
            SetSessionStatus($"被拒绝: {reason}");
        }
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

    private static Task<string?> TryCreateImageDataUrlAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mimeType = ext switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null,
        };

        if (mimeType is null)
        {
            return Task.FromResult<string?>(null);
        }

        return ReadAndEncodeAsync(path, mimeType);
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
