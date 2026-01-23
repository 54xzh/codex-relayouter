// SettingsPage：应用设置页面，包含连接配置。
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace codex_bridge.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _uiInitialized;
    private bool _suppressConfigSave = true;

    public SettingsPage()
    {
        InitializeComponent();
        _uiInitialized = true;
        Loaded += SettingsPage_Loaded;

        App.ConnectionService.ConnectionStateChanged += ConnectionService_StateChanged;
        App.ConnectionService.ConnectionClosed += ConnectionService_ConnectionClosed;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _suppressConfigSave = true;
        LoadConfigFromService();
        UpdateConnectionUI();
        _suppressConfigSave = false;
        await EnsureBackendAndConnectAsync();
    }

    private void LoadConfigFromService()
    {
        var service = App.ConnectionService;

        if (!string.IsNullOrEmpty(service.ServerUrl))
            ServerUrlTextBox.Text = service.ServerUrl;

        if (!string.IsNullOrEmpty(service.BearerToken))
            BearerTokenBox.Password = service.BearerToken;

        if (!string.IsNullOrEmpty(service.WorkingDirectory))
            WorkingDirectoryTextBox.Text = service.WorkingDirectory;

        if (!string.IsNullOrEmpty(service.Model))
            ModelTextBox.Text = service.Model;

        SetComboByTag(SandboxComboBox, service.Sandbox);
        SetComboByTag(ApprovalPolicyComboBox, service.ApprovalPolicy);
        SetComboByTag(EffortComboBox, service.Effort);
        SkipGitRepoCheckCheckBox.IsChecked = service.SkipGitRepoCheck;
    }

    private void SaveConfigToService()
    {
        var service = App.ConnectionService;

        service.ServerUrl = ServerUrlTextBox.Text?.Trim();
        service.BearerToken = GetPasswordOrNull(BearerTokenBox.Password);
        service.WorkingDirectory = GetTextOrNull(WorkingDirectoryTextBox.Text);
        service.Model = GetTextOrNull(ModelTextBox.Text);
        service.Sandbox = GetTagOrNull(SandboxComboBox);
        service.ApprovalPolicy = GetTagOrNull(ApprovalPolicyComboBox);
        service.Effort = GetTagOrNull(EffortComboBox);
        service.SkipGitRepoCheck = SkipGitRepoCheckCheckBox.IsChecked == true;
    }

    private async Task EnsureBackendAndConnectAsync()
    {
        try
        {
            if (App.ConnectionService.IsConnected)
            {
                return;
            }

            SetStatus("启动后端中…");
            await App.BackendServer.EnsureStartedAsync();

            var wsUri = App.BackendServer.WebSocketUri;
            if (wsUri is not null && string.IsNullOrWhiteSpace(ServerUrlTextBox.Text))
            {
                ServerUrlTextBox.Text = wsUri.ToString();
                App.ConnectionService.ServerUrl = wsUri.ToString();
            }

            if (!App.ConnectionService.IsConnected && wsUri is not null)
            {
                await App.ConnectionService.ConnectAsync(wsUri, CancellationToken.None);
                SetStatus("已自动连接");
            }

            UpdateConnectionUI();
        }
        catch (Exception ex)
        {
            SetStatus($"自动连接失败: {ex.Message}");
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveConfigToService();
            SetStatus("连接中…");

            Uri? uri = null;
            var uriText = ServerUrlTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(uriText))
            {
                Uri.TryCreate(uriText, UriKind.Absolute, out uri);
            }

            if (uri is null)
            {
                await App.BackendServer.EnsureStartedAsync();
                uri = App.BackendServer.WebSocketUri;
                if (uri is not null)
                {
                    ServerUrlTextBox.Text = uri.ToString();
                    App.ConnectionService.ServerUrl = uri.ToString();
                }
            }

            if (uri is null)
            {
                SetStatus("WS 地址无效");
                return;
            }

            await App.ConnectionService.ConnectAsync(uri, CancellationToken.None);
            SetStatus("已连接");
            UpdateConnectionUI();
        }
        catch (Exception ex)
        {
            SetStatus($"连接失败: {ex.Message}");
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.ConnectionService.DisconnectAsync(CancellationToken.None);
            SetStatus("已断开");
        }
        finally
        {
            UpdateConnectionUI();
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var window = App.MainWindow;
            if (window is null)
            {
                SetStatus("无法打开文件选择器");
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                WorkingDirectoryTextBox.Text = folder.Path;
                App.ConnectionService.WorkingDirectory = folder.Path;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"浏览失败: {ex.Message}");
        }
    }

    private void ConfigChanged(object sender, object e)
    {
        if (!_uiInitialized || _suppressConfigSave)
        {
            return;
        }

        SaveConfigToService();
    }

    private void ConnectionService_StateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateConnectionUI);
    }

    private void ConnectionService_ConnectionClosed(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionUI();
            SetStatus($"连接已关闭: {message}");
        });
    }

    private void UpdateConnectionUI()
    {
        var isConnected = App.ConnectionService.IsConnected;

        ConnectButton.IsEnabled = !isConnected;
        DisconnectButton.IsEnabled = isConnected;
        ConnectionStatusText.Text = isConnected ? "已连接" : "未连接";

        StatusIndicator.Fill = isConnected
            ? new SolidColorBrush(Colors.LimeGreen)
            : new SolidColorBrush(Colors.Orange);
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
    }

    private static void SetComboByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            combo.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string? GetTagOrNull(ComboBox combo)
    {
        var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static string? GetTextOrNull(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? GetPasswordOrNull(string? password)
    {
        var trimmed = password?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
