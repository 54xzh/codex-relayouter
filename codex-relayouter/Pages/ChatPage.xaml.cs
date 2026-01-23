// ChatPage：聊天页面，使用全局 ConnectionService。
using CommunityToolkit.WinUI.UI.Controls;
using codex_bridge.Bridge;
using codex_bridge.Markdown;
using codex_bridge.Models;
using codex_bridge.State;
using codex_bridge.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace codex_bridge.Pages;

public sealed partial class ChatPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object FilePathRendererMarker = new();
    private const int MaxPendingImages = 4;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const double AutoScrollBottomTolerance = 24;
    private const double ChatMessageLineHeight = 20;
    private const string ContextUsageUnavailableLabel = "-%";
    private const string StatusUnavailableLabel = "不可用";

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly HttpClient _httpClient = new();
    private int _autoConnectAttempted;
    private ScrollViewer? _messagesScrollViewer;
    private bool _scrollToBottomPending;
    private bool _forceScrollToBottomOnNextContentUpdate;
    private bool _handlersAttached;

    public ObservableCollection<ChatMessageViewModel> Messages =>
        App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId).Messages;

    public ObservableCollection<ChatImageViewModel> PendingImages { get; } = new();

    public ObservableCollection<TurnPlanStepViewModel> TurnPlanSteps =>
        App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId).TurnPlanSteps;

    public ChatPage()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        PendingImages.CollectionChanged += PendingImages_CollectionChanged;

        Loaded += ChatPage_Loaded;
        Unloaded += ChatPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ChatNavigationRequest request)
        {
            return;
        }

        App.SessionState.CurrentSessionCwd = request.Cwd;
        App.SessionState.CurrentSessionId = request.SessionId;

        try
        {
            Bindings.Update();
        }
        catch
        {
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        App.ChatStore.SetChatPageActive(false);
        DetachHandlersIfNeeded();
    }

    private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
    {
        AttachHandlersIfNeeded();
        App.ChatStore.SetChatPageActive(true);

        UpdateConnectionUI();
        ApplySessionStateToUi();
        ApplyConnectionSettingsToUi();
        UpdatePendingImagesUi();
        UpdateTurnPlanUiFromStore();
        UpdateActionButtonsVisibility();
        EnsureMessagesScrollViewer();
        ForceScrollMessagesToBottom();
        await EnsureBackendAndConnectAsync();
        await LoadSessionHistoryIfNeededAsync();
        await LoadSessionPlanIfNeededAsync();
        UpdateTurnPlanUiFromStore();
        await RefreshContextUsageAsync();
    }

    private void ChatPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.ChatStore.SetChatPageActive(false);
        DetachHandlersIfNeeded();
    }

    private void AttachHandlersIfNeeded()
    {
        if (_handlersAttached)
        {
            return;
        }

        _handlersAttached = true;

        App.ConnectionService.ConnectionStateChanged += ConnectionService_StateChanged;
        App.ConnectionService.ConnectionClosed += ConnectionService_ConnectionClosed;
        App.SessionState.CurrentSessionChanged += SessionState_CurrentSessionChanged;
        App.ChatStore.SessionContentUpdated += ChatStore_SessionContentUpdated;
        App.ChatStore.SessionPlanUpdated += ChatStore_SessionPlanUpdated;
        App.ChatStore.SessionRunStateChanged += ChatStore_SessionRunStateChanged;
    }

    private void DetachHandlersIfNeeded()
    {
        if (!_handlersAttached)
        {
            return;
        }

        _handlersAttached = false;

        App.ConnectionService.ConnectionStateChanged -= ConnectionService_StateChanged;
        App.ConnectionService.ConnectionClosed -= ConnectionService_ConnectionClosed;
        App.SessionState.CurrentSessionChanged -= SessionState_CurrentSessionChanged;
        App.ChatStore.SessionContentUpdated -= ChatStore_SessionContentUpdated;
        App.ChatStore.SessionPlanUpdated -= ChatStore_SessionPlanUpdated;
        App.ChatStore.SessionRunStateChanged -= ChatStore_SessionRunStateChanged;
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
        ApplyWorkingDirectoryOverrideToServiceIfNeeded();
        ApplyWorkingDirectoryToUi();

        var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
        var service = App.ConnectionService;

        SandboxText.Text = GetOverrideOrDefaultLabel(session.SandboxOverride, service.Sandbox);
        ModelText.Text = GetOverrideOrDefaultLabel(session.ModelOverride, service.Model);
        ThinkingText.Text = GetOverrideOrDefaultLabel(session.EffortOverride, service.Effort);
        ApprovalText.Text = GetOverrideOrDefaultLabel(session.ApprovalPolicyOverride, service.ApprovalPolicy);
    }

    private void ApplyWorkingDirectoryOverrideToServiceIfNeeded()
    {
        var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
        var overrideCwd = session.WorkingDirectoryOverride;
        if (string.IsNullOrWhiteSpace(overrideCwd))
        {
            return;
        }

        App.ConnectionService.WorkingDirectory = overrideCwd;
    }

    private static string GetOverrideOrDefaultLabel(string? overrideValue, string? defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue.Trim();
        }

        return string.IsNullOrWhiteSpace(defaultValue) ? "默认" : defaultValue.Trim();
    }

    private void ApplyWorkingDirectoryToUi()
    {
        var service = App.ConnectionService;
        var workingDirectory = service.WorkingDirectory;
        WorkspaceText.Text = GetDirectoryNameOrFallback(workingDirectory, emptyLabel: "未选择");

        var hasWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory);
        WorkspaceOpenInExplorerMenuItem.IsEnabled = hasWorkingDirectory;

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
            item.IsEnabled = !string.Equals(entry, currentWorkingDirectory, StringComparison.OrdinalIgnoreCase);

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

    private void MarkdownTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownTextBlock markdown)
        {
            return;
        }

        if (!ReferenceEquals(markdown.Tag, FilePathRendererMarker))
        {
            markdown.Tag = FilePathRendererMarker;
            markdown.SetRenderer<FilePathMarkdownRenderer>();

            // SetRenderer 不会自动刷新已渲染内容；这里通过轻量重置 Text 触发重新渲染。
            var currentText = markdown.Text;
            if (!string.IsNullOrEmpty(currentText))
            {
                markdown.Text = string.Empty;
                markdown.Text = currentText;
            }
        }

        var richTextBlock = FindDescendant<RichTextBlock>(markdown);
        if (richTextBlock is not null)
        {
            richTextBlock.LineHeight = ChatMessageLineHeight;
            richTextBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
    }

    private async void MarkdownTextBlock_LinkClicked(object sender, LinkClickedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Link) || !Uri.TryCreate(e.Link, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Ignore failures
        }
    }

    private void MarkdownTextBlock_CodeBlockResolving(object sender, CodeBlockResolvingEventArgs e)
    {
        if (sender is not MarkdownTextBlock markdown)
        {
            return;
        }

        var inlineCollection = e.InlineCollection;
        if (inlineCollection is null)
        {
            return;
        }

        var codeText = (e.Text ?? string.Empty).TrimEnd('\n', '\r');

        var foreground = markdown.CodeForeground ?? new SolidColorBrush(Colors.Black);
        var background = markdown.CodeBackground ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 248, 250));
        var borderBrush = markdown.CodeBorderBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 182, 194, 207));
        var borderThickness = markdown.CodeBorderThickness;
        var padding = markdown.CodePadding;

        if (borderThickness == default)
        {
            borderThickness = new Thickness(1);
        }

        if (padding == default)
        {
            padding = new Thickness(12, 10, 12, 10);
        }

        var codeFontFamily = markdown.CodeFontFamily ?? new FontFamily("Consolas");
        var textBox = new TextBox
        {
            Text = codeText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = markdown.WrapCodeBlock ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = codeFontFamily,
            FontSize = markdown.FontSize,
            Foreground = foreground,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            IsTabStop = false,
        };

        UIElement content = textBox;
        if (!markdown.WrapCodeBlock)
        {
            content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = textBox,
            };
        }

        var container = new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            Padding = padding,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 8, 0, 8),
            Child = content,
        };

        inlineCollection.Add(new LineBreak());
        inlineCollection.Add(new InlineUIContainer { Child = container });
        inlineCollection.Add(new LineBreak());

        e.Handled = true;
    }

    private void StatusButton_Click(object sender, RoutedEventArgs e)
    {
        StatusBackendConnectionText.Text = $"后端连接: {(App.ConnectionService.IsConnected ? "已连接" : "未连接")}";
        StatusFlyout.ShowAt(StatusButton);
        _ = RefreshContextUsageAsync();
    }

    private async Task RefreshContextUsageAsync()
    {
        try
        {
            var text = await FetchStatusTextAsync();
            await RunOnUiThreadAsync(() =>
            {
                UpdateContextUsageButton(text);
                UpdateStatusFlyout(text);
            });
        }
        catch
        {
            await RunOnUiThreadAsync(() =>
            {
                StatusButtonText.Text = ContextUsageUnavailableLabel;
                UpdateContextUsageRing(null);
                UpdateStatusFlyout(null);
            });
        }
    }

    private async Task<string?> FetchStatusTextAsync()
    {
        await App.BackendServer.EnsureStartedAsync();

        var baseUri = App.BackendServer.HttpBaseUri;
        if (baseUri is null)
        {
            return null;
        }

        var statusBuilder = new UriBuilder(new Uri(baseUri, "status"));
        var sessionId = App.SessionState.CurrentSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            statusBuilder.Query = $"sessionId={Uri.EscapeDataString(sessionId.Trim())}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, statusBuilder.Uri);

        var token = App.ConnectionService.BearerToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _httpClient.SendAsync(request, CancellationToken.None);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);

        if (!response.IsSuccessStatusCode)
        {
            var reason = string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase : text.Trim();
            return $"请求失败: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\r\n{reason}".Trim();
        }

        return text;
    }

    private void UpdateContextUsageButton(string? statusText)
    {
        if (TryExtractContextUsagePercent(statusText, out var percent))
        {
            var clamped = Math.Clamp(percent, 0, 100);
            StatusButtonText.Text = $"{clamped}%";
            UpdateContextUsageRing(clamped);
        }
        else
        {
            StatusButtonText.Text = ContextUsageUnavailableLabel;
            UpdateContextUsageRing(null);
        }
    }

    private static bool TryExtractContextUsagePercent(string? statusText, out int percent)
    {
        percent = default;
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return false;
        }

        var lines = statusText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("上下文用量:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["上下文用量:".Length..].Trim();
            var percentIndex = value.IndexOf('%', StringComparison.Ordinal);
            if (percentIndex <= 0)
            {
                return false;
            }

            var number = value[..percentIndex].Trim();
            return int.TryParse(number, out percent);
        }

        return false;
    }

    private const double ContextUsageRingSize = 14;
    private const double ContextUsageRingStrokeThickness = 2;

    private void UpdateContextUsageRing(int? percent)
    {
        StatusButtonRingArc.Visibility = Visibility.Collapsed;
        StatusButtonRingFull.Visibility = Visibility.Collapsed;
        StatusButtonRingArc.Data = null;

        if (percent is null)
        {
            return;
        }

        var clamped = Math.Clamp(percent.Value, 0, 100);
        if (clamped <= 0)
        {
            return;
        }

        if (clamped >= 100)
        {
            StatusButtonRingFull.Visibility = Visibility.Visible;
            return;
        }

        StatusButtonRingArc.Data = BuildRingArcGeometry(clamped, ContextUsageRingSize, ContextUsageRingStrokeThickness);
        StatusButtonRingArc.Visibility = Visibility.Visible;
    }

    private static Geometry BuildRingArcGeometry(int percent, double size, double strokeThickness)
    {
        var center = size / 2;
        var radius = Math.Max(0, (size - strokeThickness) / 2);

        var startAngle = -90d;
        var sweepAngle = percent / 100d * 360d;
        var endAngle = startAngle + sweepAngle;

        Point PointAt(double angleDegrees)
        {
            var radians = angleDegrees * Math.PI / 180d;
            var x = center + radius * Math.Cos(radians);
            var y = center + radius * Math.Sin(radians);
            return new Point(x, y);
        }

        var start = PointAt(startAngle);
        var end = PointAt(endAngle);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepAngle > 180d,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void UpdateStatusFlyout(string? statusText)
    {
        StatusBackendConnectionText.Text = $"后端连接: {(App.ConnectionService.IsConnected ? "已连接" : "未连接")}";

        UpdateRateLimitStatus(statusText, "5h限额:", "5h限额", StatusFiveHourSection, StatusFiveHourText, StatusFiveHourProgress);
        UpdateRateLimitStatus(statusText, "周限额:", "周限额", StatusWeeklySection, StatusWeeklyText, StatusWeeklyProgress);

        if (TryExtractContextUsagePercent(statusText, out var percent))
        {
            StatusContextUsageText.Text = $"上下文用量: {Math.Clamp(percent, 0, 100)}%";
        }
        else
        {
            StatusContextUsageText.Text = $"上下文用量: {StatusUnavailableLabel}";
        }
    }

    private void UpdateRateLimitStatus(
        string? statusText,
        string prefix,
        string displayName,
        FrameworkElement container,
        TextBlock label,
        ProgressBar progressBar)
    {
        if (!TryExtractRateLimit(statusText, prefix, out var usedPercent, out var resetsAt))
        {
            container.Visibility = Visibility.Collapsed;
            return;
        }

        if (usedPercent is null && string.IsNullOrWhiteSpace(resetsAt))
        {
            container.Visibility = Visibility.Collapsed;
            return;
        }

        container.Visibility = Visibility.Visible;

        var usedText = usedPercent is null ? StatusUnavailableLabel : $"{Math.Clamp(usedPercent.Value, 0, 100):0.#}%";
        var resetsText = string.IsNullOrWhiteSpace(resetsAt) ? StatusUnavailableLabel : resetsAt;
        label.Text = $"{displayName}: 已用 {usedText}，重置 {resetsText}";

        if (usedPercent is null)
        {
            progressBar.IsIndeterminate = true;
            progressBar.Value = 0;
            return;
        }

        progressBar.IsIndeterminate = false;
        progressBar.Value = Math.Clamp(usedPercent.Value, 0, 100);
    }

    private static bool TryExtractRateLimit(string? statusText, string prefix, out double? usedPercent, out string? resetsAt)
    {
        usedPercent = null;
        resetsAt = null;

        if (string.IsNullOrWhiteSpace(statusText))
        {
            return false;
        }

        var lines = statusText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();
            if (string.Equals(value, StatusUnavailableLabel, StringComparison.Ordinal))
            {
                return true;
            }

            var resetToken = "重置";
            var resetIndex = value.IndexOf(resetToken, StringComparison.Ordinal);
            if (resetIndex >= 0)
            {
                var resetValue = value[(resetIndex + resetToken.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(resetValue)
                    && !string.Equals(resetValue, StatusUnavailableLabel, StringComparison.Ordinal))
                {
                    resetsAt = resetValue;
                }
            }

            const string usedTokenPrimary = "已用";
            const string usedTokenFallback = "使用";

            var usedToken = value.Contains(usedTokenPrimary, StringComparison.Ordinal) ? usedTokenPrimary : usedTokenFallback;
            var usedIndex = value.IndexOf(usedToken, StringComparison.Ordinal);
            if (usedIndex >= 0)
            {
                var usedSlice = resetIndex > usedIndex
                    ? value[(usedIndex + usedToken.Length)..resetIndex]
                    : value[(usedIndex + usedToken.Length)..];

                var percentIndex = usedSlice.IndexOf('%', StringComparison.Ordinal);
                if (percentIndex > 0)
                {
                    var numberText = usedSlice[..percentIndex].Trim().TrimEnd('，', ',', ';');
                    if (TryParsePercent(numberText, out var parsed))
                    {
                        usedPercent = parsed;
                    }
                }
            }

            return true;
        }

        return false;
    }

    private static bool TryParsePercent(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
            var sessionId = App.SessionState.CurrentSessionId;
            var runId = App.ChatStore.GetActiveRunId(sessionId);
            await App.ConnectionService.SendCommandAsync("run.cancel", new { runId, sessionId }, CancellationToken.None);
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
            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);

            await service.SendCommandAsync(
                "chat.send",
                new
                {
                    prompt,
                    images,
                    sessionId = App.SessionState.CurrentSessionId,
                    workingDirectory = session.WorkingDirectoryOverride ?? service.WorkingDirectory,
                    model = session.ModelOverride ?? service.Model,
                    sandbox = session.SandboxOverride ?? service.Sandbox,
                    approvalPolicy = session.ApprovalPolicyOverride ?? service.ApprovalPolicy,
                    effort = session.EffortOverride ?? service.Effort,
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

        var sessionState = App.ChatStore.GetSessionState(sessionId);
        if (sessionState.HasLoadedHistory)
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

            var preserved = sessionState.Messages.Where(m => !string.IsNullOrWhiteSpace(m.RunId)).ToList();
            sessionState.Messages.Clear();

            var traceIndex = 0;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Role)
                    || (string.IsNullOrWhiteSpace(item.Text)
                        && (item.Images is null || item.Images.Length == 0)
                        && (item.Trace is null || item.Trace.Length == 0)))
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

                sessionState.Messages.Add(message);
            }

            foreach (var message in preserved)
            {
                sessionState.Messages.Add(message);
            }

            sessionState.HasLoadedHistory = true;
            SetSessionStatus($"会话: {sessionId}（{sessionState.Messages.Count} 条消息）");
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

    private void SessionState_CurrentSessionChanged(object? sender, EventArgs e)
    {
        try
        {
            Bindings.Update();
        }
        catch
        {
        }

        ApplySessionStateToUi();
        ApplyConnectionSettingsToUi();
        UpdateTurnPlanUiFromStore();
        UpdateActionButtonsVisibility();
        _ = LoadSessionHistoryIfNeededAsync();
        _ = LoadSessionPlanIfNeededAsync();
        ForceScrollMessagesToBottom();
    }

    private void ForceScrollMessagesToBottom()
    {
        _forceScrollToBottomOnNextContentUpdate = true;
        RequestScrollMessagesToBottom();
    }

    private void ChatStore_SessionContentUpdated(object? sender, string sessionKey)
    {
        var currentKey = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId).SessionKey;
        if (!string.Equals(currentKey, sessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureMessagesScrollViewer();
        var wasAtBottom = IsMessagesScrollAtBottom();
        if (_forceScrollToBottomOnNextContentUpdate || wasAtBottom)
        {
            _forceScrollToBottomOnNextContentUpdate = false;
            RequestScrollMessagesToBottom();
        }
    }

    private void ChatStore_SessionPlanUpdated(object? sender, string sessionKey)
    {
        var currentKey = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId).SessionKey;
        if (!string.Equals(currentKey, sessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UpdateTurnPlanUiFromStore();
    }

    private void ChatStore_SessionRunStateChanged(object? sender, string sessionKey)
    {
        var currentKey = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId).SessionKey;
        if (!string.Equals(currentKey, sessionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UpdateActionButtonsVisibility();
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

    private void UpdateTurnPlanUiFromStore()
    {
        var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
        var hasSteps = session.TurnPlanSteps.Count > 0;
        var hasExplanation = !string.IsNullOrWhiteSpace(session.TurnPlanExplanation);

        TurnPlanExplanationText.Text = hasExplanation ? session.TurnPlanExplanation!.Trim() : string.Empty;
        TurnPlanExplanationText.Visibility = hasExplanation ? Visibility.Visible : Visibility.Collapsed;

        TurnPlanSummaryText.Text = BuildTurnPlanSummary(session.TurnPlanSteps.ToList(), session.TurnPlanUpdatedAt, session.TurnPlanTurnId);

        var visible = hasSteps || hasExplanation;
        TurnPlanCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
        {
            TurnPlanExpander.IsExpanded = false;
            return;
        }

        if (!TurnPlanExpander.IsExpanded && hasSteps)
        {
            TurnPlanExpander.IsExpanded = true;
        }
    }

    private async Task LoadSessionPlanIfNeededAsync()
    {
        var sessionId = App.SessionState.CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var sessionState = App.ChatStore.GetSessionState(sessionId);
        if (sessionState.HasLoadedPlan)
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
            var path = $"api/v1/sessions/{Uri.EscapeDataString(sessionId)}/plan";
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
                ClearTurnPlanInStore(sessionState);
                UpdateTurnPlanUiFromStore();
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var snapshot = JsonSerializer.Deserialize<TurnPlanSnapshot>(json, JsonOptions);
            if (snapshot is null)
            {
                ClearTurnPlanInStore(sessionState);
                UpdateTurnPlanUiFromStore();
                return;
            }

            ApplyTurnPlanSnapshotToStore(sessionState, snapshot);
            UpdateTurnPlanUiFromStore();
        }
        catch (Exception ex)
        {
            SetSessionStatus($"加载会话计划失败: {ex.Message}");
        }
    }

    private static void ClearTurnPlanInStore(ChatSessionState sessionState)
    {
        sessionState.TurnPlanSteps.Clear();
        sessionState.TurnPlanExplanation = null;
        sessionState.TurnPlanUpdatedAt = null;
        sessionState.TurnPlanTurnId = null;
        sessionState.HasLoadedPlan = true;
    }

    private static void ApplyTurnPlanSnapshotToStore(ChatSessionState sessionState, TurnPlanSnapshot snapshot)
    {
        sessionState.TurnPlanExplanation = string.IsNullOrWhiteSpace(snapshot.Explanation) ? null : snapshot.Explanation.Trim();
        sessionState.TurnPlanUpdatedAt = snapshot.UpdatedAt;
        sessionState.TurnPlanTurnId = string.IsNullOrWhiteSpace(snapshot.TurnId) ? null : snapshot.TurnId.Trim();
        sessionState.HasLoadedPlan = true;

        sessionState.TurnPlanSteps.Clear();
        foreach (var entry in snapshot.Plan)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Step))
            {
                continue;
            }

            sessionState.TurnPlanSteps.Add(new TurnPlanStepViewModel(entry.Step.Trim(), entry.Status?.Trim() ?? string.Empty));
        }
    }

    private static string BuildTurnPlanSummary(IReadOnlyList<TurnPlanStepViewModel> steps, DateTimeOffset? updatedAt, string? turnId)
    {
        if (steps.Count == 0 && !updatedAt.HasValue)
        {
            return string.Empty;
        }

        var total = steps.Count;
        var completed = steps.Count(s => s.IsCompleted);
        var inProgress = steps.Count(s => s.IsInProgress);
        var pending = total - completed - inProgress;

        var parts = new List<string>(capacity: 4);
        if (total > 0)
        {
            parts.Add($"已完成 {completed}/{total}");
        }

        if (inProgress > 0)
        {
            parts.Add($"进行中 {inProgress}");
        }

        if (pending > 0)
        {
            parts.Add($"待处理 {pending}");
        }

        if (updatedAt.HasValue)
        {
            parts.Add($"更新 {updatedAt.Value.ToLocalTime():HH:mm}");
        }

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            parts.Add(turnId.Trim());
        }

        return string.Join(" · ", parts);
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

    private void ApplySessionStateToUi()
    {
        var sessionId = App.SessionState.CurrentSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            SetSessionStatus($"会话: {sessionId}");
            return;
        }

        SetSessionStatus("新聊天");
    }

    private void UpdateActionButtonsVisibility()
    {
        var isRunning = !string.IsNullOrWhiteSpace(App.ChatStore.GetActiveRunId(App.SessionState.CurrentSessionId));
        SendButton.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
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

        var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
        session.WorkingDirectoryOverride = workingDirectory;
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

            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
            session.WorkingDirectoryOverride = folder.Path;
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
            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
            session.SandboxOverride = string.IsNullOrEmpty(value) ? null : value;
            ApplyConnectionSettingsToUi();
        }
    }

    private void ApprovalOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
            session.ApprovalPolicyOverride = string.IsNullOrEmpty(value) ? null : value;
            ApplyConnectionSettingsToUi();
        }
    }

    private void ModelOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
            session.ModelOverride = string.IsNullOrEmpty(value) ? null : value;
            ApplyConnectionSettingsToUi();
        }
    }

    private void ThinkingOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string value)
        {
            var session = App.ChatStore.GetSessionState(App.SessionState.CurrentSessionId);
            session.EffortOverride = string.IsNullOrEmpty(value) ? null : value;
            ApplyConnectionSettingsToUi();
        }
    }
}
