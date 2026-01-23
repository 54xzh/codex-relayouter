// SessionsPage：会话管理页面（会话列表/创建/选择并跳转聊天）。
using codex_bridge.Models;
using codex_bridge.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.Pages;

public sealed partial class SessionsPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = new();
    private bool _loadedOnce;

    public ObservableCollection<SessionSummaryViewModel> Sessions { get; } = new();

    public SessionsPage()
    {
        InitializeComponent();
        Loaded += SessionsPage_Loaded;
    }

    private async void SessionsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        await EnsureBackendAndRefreshAsync();
    }

    private async Task EnsureBackendAndRefreshAsync()
    {
        try
        {
            SetStatus("启动后端中…");
            await App.BackendServer.EnsureStartedAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}");
        }
    }

    private async Task RefreshAsync()
    {
        var baseUri = App.BackendServer.HttpBaseUri;
        if (baseUri is null)
        {
            SetStatus("后端未就绪");
            return;
        }

        var uri = new Uri(baseUri, "api/v1/sessions?limit=50");

        SetStatus("加载会话…");
        using var response = await _httpClient.GetAsync(uri, CancellationToken.None);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var items = JsonSerializer.Deserialize<SessionSummary[]>(json, JsonOptions) ?? Array.Empty<SessionSummary>();

        Sessions.Clear();
        foreach (var item in items)
        {
            var title = string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title;
            var vm = new SessionSummaryViewModel(item.Id, title, item.CreatedAt, item.Cwd, item.Originator, item.CliVersion)
            {
                IsHidden = App.SessionPreferences.IsHidden(item.Id),
                IsPinned = App.SessionPreferences.IsPinned(item.Id),
            };
            Sessions.Add(vm);
        }

        SetStatus($"已加载 {Sessions.Count} 个会话");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureBackendAndRefreshAsync();
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.BackendServer.EnsureStartedAsync();

            var baseUri = App.BackendServer.HttpBaseUri;
            if (baseUri is null)
            {
                SetStatus("后端未就绪");
                return;
            }

            var cwd = CwdTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(cwd))
            {
                cwd = App.SessionState.CurrentSessionCwd;
            }

            if (string.IsNullOrWhiteSpace(cwd))
            {
                SetStatus("创建失败: cwd 不能为空");
                return;
            }

            var uri = new Uri(baseUri, "api/v1/sessions");
            var payload = JsonSerializer.Serialize(new { cwd }, JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(uri, content, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var created = JsonSerializer.Deserialize<SessionSummary>(json, JsonOptions);
            if (created is null)
            {
                SetStatus("创建失败: 响应为空");
                return;
            }

            App.SessionState.CurrentSessionCwd = created.Cwd;
            App.SessionState.CurrentSessionId = created.Id;

            SetStatus($"已创建会话: {created.Id}");
            await RefreshAsync();

            Frame.Navigate(typeof(ChatPage));
        }
        catch (Exception ex)
        {
            SetStatus($"创建失败: {ex.Message}");
        }
    }

    private void SessionsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SessionSummaryViewModel session)
        {
            return;
        }

        App.SessionState.CurrentSessionCwd = session.Cwd;
        App.SessionState.CurrentSessionId = session.Id;
        SetStatus($"已选择会话: {session.Id}");

        Frame.Navigate(typeof(ChatPage));
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
    }

    private void SessionItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SessionSummaryViewModel session)
        {
            return;
        }

        var flyout = new MenuFlyout();

        // Pin/Unpin
        var pinItem = new MenuFlyoutItem
        {
            Text = session.IsPinned ? "✓ 已置顶" : "置顶",
            Icon = new FontIcon { Glyph = "\uE718" },
        };
        pinItem.Click += async (s, args) =>
        {
            await App.SessionPreferences.TogglePinnedAsync(session.Id);
            session.IsPinned = App.SessionPreferences.IsPinned(session.Id);
            await RefreshAsync();
        };
        flyout.Items.Add(pinItem);

        // Hide/Unhide
        var hideItem = new MenuFlyoutItem
        {
            Text = session.IsHidden ? "✓ 已隐藏" : "隐藏",
            Icon = new FontIcon { Glyph = "\uED1A" },
        };
        hideItem.Click += async (s, args) =>
        {
            await App.SessionPreferences.ToggleHiddenAsync(session.Id);
            session.IsHidden = App.SessionPreferences.IsHidden(session.Id);
            await RefreshAsync();
        };
        flyout.Items.Add(hideItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Delete
        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除...",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        deleteItem.Click += async (s, args) => await ConfirmAndDeleteSessionAsync(session.Id);
        flyout.Items.Add(deleteItem);

        flyout.ShowAt(element, e.GetPosition(element));
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
            XamlRoot = XamlRoot,
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
                SetStatus("后端未就绪");
                return;
            }

            var uri = new Uri(baseUri, $"api/v1/sessions/{sessionId}");
            using var response = await _httpClient.DeleteAsync(uri, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            await App.SessionPreferences.RemoveSessionAsync(sessionId);
            SetStatus($"已删除会话: {sessionId}");
            await RefreshAsync();

            // Also refresh sidebar in MainWindow
            if (App.MainWindow is MainWindow mw)
            {
                await mw.RefreshRecentSessionsAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败: {ex.Message}");
        }
    }
}
