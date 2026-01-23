using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using codex_bridge.Bridge;
using codex_bridge.Models;
using codex_bridge.Pages;
using codex_bridge.State;
using codex_bridge.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge
{
    public sealed partial class MainWindow : Window
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly HttpClient _httpClient = new();
        private readonly DispatcherQueue _dispatcherQueue;
        private int _sessionLimit = 20;
        private bool _hasMoreSessions = true;
        private readonly SemaphoreSlim _recentSessionsRefreshGate = new(1, 1);
        private readonly object _pairingGate = new();
        private readonly Queue<PairingRequestInfo> _pendingPairingRequests = new();
        private bool _pairingDialogOpen;
        private readonly object _approvalGate = new();
        private readonly Queue<ApprovalRequestInfo> _pendingApprovalRequests = new();
        private bool _approvalDialogOpen;

        public ObservableCollection<SessionSummaryViewModel> RecentSessions { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            WindowSizing.ApplyStartupSizingAndCenter(this);

            App.ChatStore.Initialize(_dispatcherQueue);
            App.ChatStore.Attach(App.ConnectionService);

            // Load preferences and recent sessions on startup
            _ = InitializeAsync();

            App.ConnectionService.EnvelopeReceived += ConnectionService_EnvelopeReceived;
            App.SessionState.CurrentSessionChanged += SessionState_CurrentSessionChanged;
            App.ChatStore.SessionIndicatorChanged += ChatStore_SessionIndicatorChanged;
            Closed += (_, _) =>
            {
                App.SessionState.CurrentSessionChanged -= SessionState_CurrentSessionChanged;
                App.ChatStore.SessionIndicatorChanged -= ChatStore_SessionIndicatorChanged;
            };
            ContentFrame.Navigated += (_, _) => UpdateChatSidebarSelection();

            Navigate("chat");
        }

        private async void SessionState_CurrentSessionChanged(object? sender, EventArgs e)
        {
            _dispatcherQueue.TryEnqueue(UpdateChatSidebarSelection);

            var sessionId = App.SessionState.CurrentSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            if (RecentSessions.Any(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal)))
            {
                return;
            }

            if (!_recentSessionsRefreshGate.Wait(0))
            {
                return;
            }

            try
            {
                await LoadRecentSessionsAsync();
            }
            catch
            {
                // Ignore refresh errors to avoid impacting the current chat flow.
            }
            finally
            {
                _recentSessionsRefreshGate.Release();
            }
        }

        private void UpdateChatSidebarSelection()
        {
            if (ContentFrame.CurrentSourcePageType != typeof(ChatPage))
            {
                return;
            }

            var sessionId = App.SessionState.CurrentSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (!ReferenceEquals(NavView.SelectedItem, NewChatItem))
                {
                    NavView.SelectedItem = NewChatItem;
                }
                return;
            }

            var tagToSelect = $"session:{sessionId}";
            foreach (var menuItem in NavView.MenuItems)
            {
                if (menuItem is not NavigationViewItem item)
                {
                    continue;
                }

                if (item.Tag is not string tag || !string.Equals(tag, tagToSelect, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!ReferenceEquals(NavView.SelectedItem, item))
                {
                    NavView.SelectedItem = item;
                }
                return;
            }
        }

        private void ConnectionService_EnvelopeReceived(object? sender, BridgeEnvelope envelope)
        {
            if (!string.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(envelope.Name, "device.pairing.requested", StringComparison.OrdinalIgnoreCase))
            {
                if (envelope.Data.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                if (!TryGetString(envelope.Data, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                TryGetString(envelope.Data, "deviceName", out var deviceName);
                TryGetString(envelope.Data, "platform", out var platform);
                TryGetString(envelope.Data, "deviceModel", out var deviceModel);
                TryGetString(envelope.Data, "appVersion", out var appVersion);
                TryGetString(envelope.Data, "clientIp", out var clientIp);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    EnqueuePairingRequest(new PairingRequestInfo(
                        RequestId: requestId.Trim(),
                        DeviceName: deviceName?.Trim(),
                        Platform: platform?.Trim(),
                        DeviceModel: deviceModel?.Trim(),
                        AppVersion: appVersion?.Trim(),
                        ClientIp: clientIp?.Trim()));
                });

                return;
            }

            if (string.Equals(envelope.Name, "approval.requested", StringComparison.OrdinalIgnoreCase))
            {
                if (envelope.Data.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                if (!TryGetString(envelope.Data, "runId", out var runId) || string.IsNullOrWhiteSpace(runId))
                {
                    return;
                }

                if (!TryGetString(envelope.Data, "requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                if (!TryGetString(envelope.Data, "kind", out var kind) || string.IsNullOrWhiteSpace(kind))
                {
                    return;
                }

                var reason = TryGetString(envelope.Data, "reason", out var reasonText) ? reasonText : null;
                var itemId = TryGetString(envelope.Data, "itemId", out var itemIdText) ? itemIdText : null;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    EnqueueApprovalRequest(new ApprovalRequestInfo(
                        RunId: runId.Trim(),
                        RequestId: requestId.Trim(),
                        Kind: kind.Trim(),
                        Reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                        ItemId: string.IsNullOrWhiteSpace(itemId) ? null : itemId.Trim()));
                });
            }
        }

        private void EnqueuePairingRequest(PairingRequestInfo request)
        {
            lock (_pairingGate)
            {
                if (_pendingPairingRequests.Any(r => string.Equals(r.RequestId, request.RequestId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                _pendingPairingRequests.Enqueue(request);

                if (_pairingDialogOpen)
                {
                    return;
                }

                _pairingDialogOpen = true;
            }

            _ = ShowNextPairingDialogAsync();
        }

        private async Task ShowNextPairingDialogAsync()
        {
            while (true)
            {
                PairingRequestInfo? next = null;
                lock (_pairingGate)
                {
                    if (_pendingPairingRequests.Count > 0)
                    {
                        next = _pendingPairingRequests.Dequeue();
                    }
                    else
                    {
                        _pairingDialogOpen = false;
                        return;
                    }
                }

                if (next is null)
                {
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(next.DeviceName) ? "未知设备" : next.DeviceName;
                var meta = string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(next.Platform) ? null : next.Platform,
                    string.IsNullOrWhiteSpace(next.DeviceModel) ? null : next.DeviceModel,
                    string.IsNullOrWhiteSpace(next.AppVersion) ? null : $"v{next.AppVersion}",
                    string.IsNullOrWhiteSpace(next.ClientIp) ? null : next.ClientIp,
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                var content = string.IsNullOrWhiteSpace(meta)
                    ? "检测到新的设备配对请求。是否允许该设备连接到本机后端？"
                    : $"检测到新的设备配对请求：{meta}\n\n是否允许该设备连接到本机后端？";

                var dialog = new ContentDialog
                {
                    Title = $"允许设备连接：{title}？",
                    Content = content,
                    PrimaryButtonText = "允许",
                    CloseButtonText = "拒绝",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };

                var result = await dialog.ShowAsync();
                var decision = result == ContentDialogResult.Primary ? "approve" : "decline";

                try
                {
                    await App.BackendServer.EnsureStartedAsync();

                    var baseUri = App.BackendServer.HttpBaseUri;
                    if (baseUri is null)
                    {
                        continue;
                    }

                    var uri = new Uri(baseUri, $"api/v1/connections/pairings/{next.RequestId}/respond");
                    var payload = JsonSerializer.Serialize(new { decision }, JsonOptions);
                    using var httpContent = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = await _httpClient.PostAsync(uri, httpContent, CancellationToken.None);
                    response.EnsureSuccessStatusCode();
                }
                catch
                {
                }
            }
        }

        private void EnqueueApprovalRequest(ApprovalRequestInfo request)
        {
            lock (_approvalGate)
            {
                if (_pendingApprovalRequests.Any(r =>
                        string.Equals(r.RunId, request.RunId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.RequestId, request.RequestId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                _pendingApprovalRequests.Enqueue(request);

                if (_approvalDialogOpen)
                {
                    return;
                }

                _approvalDialogOpen = true;
            }

            _ = ShowNextApprovalDialogAsync();
        }

        private async Task ShowNextApprovalDialogAsync()
        {
            while (true)
            {
                ApprovalRequestInfo? next = null;
                lock (_approvalGate)
                {
                    if (_pendingApprovalRequests.Count > 0)
                    {
                        next = _pendingApprovalRequests.Dequeue();
                    }
                    else
                    {
                        _approvalDialogOpen = false;
                        return;
                    }
                }

                if (next is null)
                {
                    continue;
                }

                var acceptForSessionCheckBox = new CheckBox
                {
                    Content = "本会话内自动允许同类操作",
                    IsChecked = false,
                };

                var body = new StackPanel { Spacing = 8 };
                if (!string.IsNullOrWhiteSpace(next.Reason))
                {
                    body.Children.Add(new TextBlock { Text = next.Reason, TextWrapping = TextWrapping.Wrap });
                }

                if (!string.IsNullOrWhiteSpace(next.ItemId))
                {
                    body.Children.Add(new TextBlock { Opacity = 0.7, Text = $"itemId: {next.ItemId}" });
                }

                body.Children.Add(acceptForSessionCheckBox);

                var title = string.Equals(next.Kind, "commandExecution", StringComparison.OrdinalIgnoreCase)
                    ? "需要批准：执行命令"
                    : "需要批准：修改文件";

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = body,
                    PrimaryButtonText = "允许",
                    SecondaryButtonText = "拒绝",
                    CloseButtonText = "取消任务",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
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
                        new { runId = next.RunId, requestId = next.RequestId, decision },
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        private sealed record ApprovalRequestInfo(string RunId, string RequestId, string Kind, string? Reason, string? ItemId);

        private static bool TryGetString(JsonElement data, string propertyName, out string? value)
        {
            value = null;

            if (data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!data.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = prop.GetString();
            return true;
        }

        private async Task InitializeAsync()
        {
            await App.SessionPreferences.LoadAsync();
            await LoadRecentSessionsAsync();
        }

        private async Task LoadRecentSessionsAsync(bool append = false)
        {
            try
            {
                await App.BackendServer.EnsureStartedAsync();

                var baseUri = App.BackendServer.HttpBaseUri;
                if (baseUri is null)
                {
                    return;
                }

                var uri = new Uri(baseUri, $"api/v1/sessions?limit={_sessionLimit}");
                using var response = await _httpClient.GetAsync(uri, CancellationToken.None);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
                var items = JsonSerializer.Deserialize<SessionSummary[]>(json, JsonOptions) ?? Array.Empty<SessionSummary>();

                DispatcherQueue.TryEnqueue(() =>
                {
                    RecentSessions.Clear();

                    foreach (var item in items)
                    {
                        var title = string.IsNullOrWhiteSpace(item.Title) ? "未命名会话" : item.Title;
                        var vm = new SessionSummaryViewModel(item.Id, title, item.CreatedAt, item.Cwd, item.Originator, item.CliVersion)
                        {
                            IsHidden = App.SessionPreferences.IsHidden(item.Id),
                            IsPinned = App.SessionPreferences.IsPinned(item.Id),
                        };
                        RecentSessions.Add(vm);
                    }

                    // Check if there are more sessions to load
                    _hasMoreSessions = items.Length >= _sessionLimit;

                    UpdateSidebarSessions();
                });
            }
            catch
            {
                // Silently ignore loading errors
            }
        }

        private void UpdateSidebarSessions()
        {
            // Find the header index
            int headerIndex = -1;
            for (int i = 0; i < NavView.MenuItems.Count; i++)
            {
                if (NavView.MenuItems[i] is NavigationViewItemHeader)
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                return;
            }

            // Remove existing session items (after header)
            while (NavView.MenuItems.Count > headerIndex + 1)
            {
                NavView.MenuItems.RemoveAt(headerIndex + 1);
            }

            // Add new session items (pinned first, then filter hidden, sort by time)
            var sortedSessions = RecentSessions
                .Where(s => !s.IsHidden)
                .OrderByDescending(s => s.IsPinned)
                .ThenByDescending(s => s.CreatedAt)
                .ToList();

            foreach (var session in sortedSessions)
            {
                var indicator = App.ChatStore.GetIndicator(session.Id);
                var item = new NavigationViewItem
                {
                    Tag = $"session:{session.Id}",
                    Content = CreateSessionNavContent(session.IsPinned ? $"[置顶] {session.Title}" : session.Title, indicator),
                    Icon = new SymbolIcon(Symbol.Message),
                    ContextFlyout = CreateSessionContextMenu(session),
                };
                ToolTipService.SetToolTip(item, session.Subtitle);
                NavView.MenuItems.Add(item);
            }

            // Add "Load More" button if there are more sessions
            if (_hasMoreSessions)
            {
                var loadMoreItem = new NavigationViewItem
                {
                    Tag = "loadmore",
                    Content = new TextBlock
                    {
                        Text = "加载更多...",
                        Opacity = 0.7,
                    },
                    Icon = new SymbolIcon(Symbol.More),
                    SelectsOnInvoked = false,
                };
                NavView.MenuItems.Add(loadMoreItem);
            }

            UpdateChatSidebarSelection();
        }

        private void ChatStore_SessionIndicatorChanged(object? sender, string sessionId)
        {
            _dispatcherQueue.TryEnqueue(UpdateSidebarSessions);
        }

        private UIElement CreateSessionNavContent(string title, SessionIndicatorState indicator)
        {
            var grid = new Grid
            {
                ColumnSpacing = 10,
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            grid.Children.Add(text);

            FrameworkElement? indicatorElement = CreateIndicatorElement(indicator);
            if (indicatorElement is not null)
            {
                Grid.SetColumn(indicatorElement, 1);
                grid.Children.Add(indicatorElement);
            }

            return grid;
        }

        private FrameworkElement? CreateIndicatorElement(SessionIndicatorState indicator)
        {
            if (indicator == SessionIndicatorState.Running)
            {
                return new ProgressRing
                {
                    IsActive = true,
                    IsIndeterminate = true,
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            if (indicator == SessionIndicatorState.Completed)
            {
                return new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(Colors.LimeGreen),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            if (indicator == SessionIndicatorState.Warning)
            {
                var brush = Application.Current.Resources["SystemFillColorCautionBrush"] as Brush;
                return new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = brush ?? new SolidColorBrush(Colors.Goldenrod),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return null;
        }

        public async Task RefreshRecentSessionsAsync()
        {
            _sessionLimit = 20;
            _hasMoreSessions = true;
            await LoadRecentSessionsAsync();
        }

        private async void HandleLoadMore()
        {
            _sessionLimit += 10;
            await LoadRecentSessionsAsync();
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is not NavigationViewItem item)
            {
                return;
            }

            var tag = item.Tag?.ToString();
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            if (tag == "loadmore")
            {
                HandleLoadMore();
                return;
            }

            if (tag == "newchat")
            {
                HandleNewChat(args.RecommendedNavigationTransitionInfo);
                return;
            }

            if (tag.StartsWith("session:"))
            {
                var sessionId = tag.Substring("session:".Length);
                HandleSelectSession(sessionId, args.RecommendedNavigationTransitionInfo);
                return;
            }

            Navigate(tag, transitionInfo: args.RecommendedNavigationTransitionInfo);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // Only handle footer items via selection change
            if (args.SelectedItem is not NavigationViewItem item)
            {
                return;
            }

            var tag = item.Tag?.ToString();
            if (string.IsNullOrEmpty(tag) || tag == "newchat" || tag.StartsWith("session:"))
            {
                return;
            }

            Navigate(tag, transitionInfo: args.RecommendedNavigationTransitionInfo);
        }

        private void HandleNewChat(NavigationTransitionInfo? transitionInfo)
        {
            NavView.SelectedItem = NewChatItem;
            Navigate(
                "chat",
                parameter: new ChatNavigationRequest(SessionId: null, Cwd: null),
                transitionInfo: transitionInfo,
                force: true);
        }

        private void HandleSelectSession(string sessionId, NavigationTransitionInfo? transitionInfo)
        {
            var session = FindSessionById(sessionId);
            if (session is not null)
            {
                Navigate(
                    "chat",
                    parameter: new ChatNavigationRequest(SessionId: session.Id, Cwd: session.Cwd),
                    transitionInfo: transitionInfo,
                    force: true);
                return;
            }

            Navigate("chat", transitionInfo: transitionInfo, force: true);
        }

        private SessionSummaryViewModel? FindSessionById(string sessionId)
        {
            foreach (var session in RecentSessions)
            {
                if (session.Id == sessionId)
                {
                    return session;
                }
            }
            return null;
        }

        private void Navigate(
            string? tag,
            object? parameter = null,
            NavigationTransitionInfo? transitionInfo = null,
            bool force = false)
        {
            var target = tag switch
            {
                "chat" => typeof(ChatPage),
                "sessions" => typeof(SessionsPage),
                "connections" => typeof(ConnectionsPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(ChatPage),
            };

            if (!force && ContentFrame.CurrentSourcePageType == target)
            {
                return;
            }

            if (transitionInfo is null)
            {
                ContentFrame.Navigate(target, parameter);
                return;
            }

            ContentFrame.Navigate(target, parameter, transitionInfo);
        }

        private sealed record PairingRequestInfo(
            string RequestId,
            string? DeviceName,
            string? Platform,
            string? DeviceModel,
            string? AppVersion,
            string? ClientIp);

        private MenuFlyout CreateSessionContextMenu(SessionSummaryViewModel session)
        {
            var flyout = new MenuFlyout();

            // Pin/Unpin
            var pinItem = new MenuFlyoutItem
            {
                Text = session.IsPinned ? "✓ 已置顶" : "置顶",
                Icon = new FontIcon { Glyph = "\uE718" },
            };
            pinItem.Click += async (s, e) => await TogglePinSessionAsync(session.Id);
            flyout.Items.Add(pinItem);

            // Hide
            var hideItem = new MenuFlyoutItem
            {
                Text = "隐藏",
                Icon = new FontIcon { Glyph = "\uED1A" },
            };
            hideItem.Click += async (s, e) => await HideSessionAsync(session.Id);
            flyout.Items.Add(hideItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // Delete
            var deleteItem = new MenuFlyoutItem
            {
                Text = "删除...",
                Icon = new FontIcon { Glyph = "\uE74D" },
            };
            deleteItem.Click += async (s, e) => await ConfirmAndDeleteSessionAsync(session.Id);
            flyout.Items.Add(deleteItem);

            return flyout;
        }

        private async Task TogglePinSessionAsync(string sessionId)
        {
            await App.SessionPreferences.TogglePinnedAsync(sessionId);
            var session = FindSessionById(sessionId);
            if (session is not null)
            {
                session.IsPinned = App.SessionPreferences.IsPinned(sessionId);
            }
            UpdateSidebarSessions();
        }

        private async Task HideSessionAsync(string sessionId)
        {
            await App.SessionPreferences.SetHiddenAsync(sessionId, true);
            var session = FindSessionById(sessionId);
            if (session is not null)
            {
                session.IsHidden = true;
            }
            UpdateSidebarSessions();
        }

        private async Task ConfirmAndDeleteSessionAsync(string sessionId)
        {
            var dialog = new ContentDialog
            {
                Title = "确定要删除这个会话吗？",
                Content = "会话文件将被移至回收站。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                var baseUri = App.BackendServer.HttpBaseUri;
                if (baseUri is null)
                {
                    return;
                }

                var uri = new Uri(baseUri, $"api/v1/sessions/{sessionId}");
                using var response = await _httpClient.DeleteAsync(uri, CancellationToken.None);
                response.EnsureSuccessStatusCode();

                // Remove from preferences
                await App.SessionPreferences.RemoveSessionAsync(sessionId);

                // Refresh the list
                await RefreshRecentSessionsAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "删除失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                };
                await errorDialog.ShowAsync();
            }
        }
    }
}
