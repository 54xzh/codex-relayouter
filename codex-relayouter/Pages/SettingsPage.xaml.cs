// SettingsPage：应用设置页面，包含连接配置。
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using codex_bridge.Backend;
using codex_bridge.IO;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.System;

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
        LoadTranslationConfigFromBackend();
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

    private void LoadTranslationConfigFromBackend()
    {
        var settings = App.BackendServer.TranslationSettings;

        TranslationEnabledToggleSwitch.IsOn = settings.Enabled;
        TranslationBaseUrlTextBox.Text = settings.BaseUrl ?? string.Empty;
        TranslationApiKeyBox.Password = settings.ApiKey ?? string.Empty;
        TranslationModelTextBox.Text = settings.Model ?? string.Empty;
        TranslationMaxRequestsPerSecondTextBox.Text = settings.MaxRequestsPerSecond.ToString();
        TranslationMaxConcurrencyTextBox.Text = settings.MaxConcurrency.ToString();
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

    private async void ApplyTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var maxRequestsPerSecond = ParsePositiveIntOrDefault(TranslationMaxRequestsPerSecondTextBox.Text, defaultValue: 1);
            var maxConcurrency = ParsePositiveIntOrDefault(TranslationMaxConcurrencyTextBox.Text, defaultValue: 2);

            TranslationMaxRequestsPerSecondTextBox.Text = maxRequestsPerSecond.ToString();
            TranslationMaxConcurrencyTextBox.Text = maxConcurrency.ToString();

            var settings = new BackendTranslationSettings(
                Enabled: TranslationEnabledToggleSwitch.IsOn,
                BaseUrl: GetTextOrNull(TranslationBaseUrlTextBox.Text),
                ApiKey: GetPasswordOrNull(TranslationApiKeyBox.Password),
                Model: GetTextOrNull(TranslationModelTextBox.Text),
                MaxRequestsPerSecond: maxRequestsPerSecond,
                MaxConcurrency: maxConcurrency);

            var existingServerUrl = ServerUrlTextBox.Text?.Trim();
            var oldBackendUri = App.BackendServer.WebSocketUri;
            var wasConnectedToLocalBackend =
                oldBackendUri is not null
                && !string.IsNullOrWhiteSpace(existingServerUrl)
                && string.Equals(existingServerUrl, oldBackendUri.ToString(), StringComparison.OrdinalIgnoreCase)
                && App.ConnectionService.IsConnected;

            var shouldConnectToLocalBackend = string.IsNullOrWhiteSpace(existingServerUrl) || wasConnectedToLocalBackend;

            SetStatus("应用翻译设置中…");
            await App.BackendServer.SetTranslationSettingsAsync(settings);

            LoadTranslationConfigFromBackend();

            var newBackendUri = App.BackendServer.WebSocketUri;
            if (newBackendUri is not null && shouldConnectToLocalBackend)
            {
                ServerUrlTextBox.Text = newBackendUri.ToString();
                App.ConnectionService.ServerUrl = newBackendUri.ToString();

                await App.ConnectionService.ConnectAsync(newBackendUri, CancellationToken.None);
                SetStatus("翻译设置已应用（后端已重启并重新连接）");
            }
            else
            {
                SetStatus("翻译设置已应用（后端已重启）");
            }

            UpdateConnectionUI();
        }
        catch (Exception ex)
        {
            SetStatus($"应用翻译设置失败: {ex.Message}");
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

    private async void ViewBackendLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.BackendServer.EnsureStartedAsync();
        }
        catch
        {
        }

        var logPath = App.BackendServer.LogFilePath;

        var header = new TextBlock
        {
            Text = logPath,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        var openFolder = new HyperlinkButton
        {
            Content = "打开日志文件夹",
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var logBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 320,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(logBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(logBox, ScrollBarVisibility.Auto);

        var panel = new StackPanel
        {
            Spacing = 8,
        };
        panel.Children.Add(header);
        panel.Children.Add(openFolder);
        panel.Children.Add(logBox);

        var dialog = new ContentDialog
        {
            Title = "后端日志",
            Content = panel,
            PrimaryButtonText = "刷新",
            SecondaryButtonText = "复制",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        async Task RefreshAsync()
        {
            try
            {
                var info = File.Exists(logPath) ? new FileInfo(logPath) : null;
                if (info is not null)
                {
                    header.Text = $"{logPath}（{info.Length / 1024:N0} KB，{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}）";
                }
                else
                {
                    header.Text = logPath;
                }

                var text = await LogTailReader.ReadTailAsync(logPath, maxBytes: 256 * 1024, maxLines: 2000);
                logBox.Text = string.IsNullOrWhiteSpace(text) ? "暂无日志。" : text;
                logBox.SelectionStart = logBox.Text.Length;
                logBox.SelectionLength = 0;
            }
            catch (Exception ex)
            {
                logBox.Text = $"读取日志失败: {ex.Message}";
            }
        }

        openFolder.Click += async (_, _) =>
        {
            try
            {
                var folderPath = Path.GetDirectoryName(logPath);
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    return;
                }

                var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                await Launcher.LaunchFolderAsync(folder);
            }
            catch (Exception ex)
            {
                SetStatus($"打开文件夹失败: {ex.Message}");
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await RefreshAsync();
        };

        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var data = new DataPackage();
                data.SetText(logBox.Text ?? string.Empty);
                Clipboard.SetContent(data);
            }
            catch
            {
            }
        };

        await RefreshAsync();
        await dialog.ShowAsync();
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

    private static int ParsePositiveIntOrDefault(string? text, int defaultValue)
    {
        if (int.TryParse(text?.Trim(), out var value) && value > 0)
        {
            return value;
        }

        return defaultValue;
    }
}
