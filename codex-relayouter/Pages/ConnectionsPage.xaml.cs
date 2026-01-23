// ConnectionsPage：连接管理页（局域网共享开关、配对二维码、已配对设备列表与撤销）。
using codex_bridge.Models;
using codex_bridge.Networking;
using codex_bridge.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace codex_bridge.Pages;

public sealed partial class ConnectionsPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HttpClient _httpClient = new();
    private bool _loadedOnce;
    private bool _suppressLanToggle;
    private string? _currentPairingCode;
    private string? _currentPairingUri;

    public ObservableCollection<PairedDeviceViewModel> Devices { get; } = new();

    public ConnectionsPage()
    {
        InitializeComponent();
        Loaded += ConnectionsPage_Loaded;
    }

    private async void ConnectionsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;

        await EnsureBackendAsync();

        _suppressLanToggle = true;
        LanEnabledToggle.IsOn = App.BackendServer.IsLanEnabled;
        _suppressLanToggle = false;

        CopyPairingButton.IsEnabled = LanEnabledToggle.IsOn;

        // 自动生成二维码
        if (LanEnabledToggle.IsOn)
        {
            await GeneratePairingAsync();
        }

        await RefreshDevicesAsync();
    }

    private async Task EnsureBackendAsync()
    {
        try
        {
            SetStatus("启动后端中…");
            await App.BackendServer.EnsureStartedAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"后端启动失败: {ex.Message}");
        }
    }

    private async void LanEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce || _suppressLanToggle)
        {
            return;
        }

        var enable = LanEnabledToggle.IsOn;

        try
        {
            CopyPairingButton.IsEnabled = false;
            SetStatus(enable ? "启用局域网共享…" : "关闭局域网共享…");

            if (App.ConnectionService.IsConnected)
            {
                await App.ConnectionService.DisconnectAsync(CancellationToken.None);
            }

            await App.BackendServer.SetLanEnabledAsync(enable);

            var wsUri = App.BackendServer.WebSocketUri;
            if (wsUri is not null)
            {
                App.ConnectionService.ServerUrl = wsUri.ToString();
                await App.ConnectionService.ConnectAsync(wsUri, CancellationToken.None);
            }

            CopyPairingButton.IsEnabled = enable;

            if (enable)
            {
                // 自动生成二维码
                await GeneratePairingAsync();
            }
            else
            {
                ClearPairingUi();
            }

            await RefreshDevicesAsync();
            SetStatus(enable ? "局域网共享已启用" : "局域网共享已关闭");
        }
        catch (Exception ex)
        {
            SetStatus($"切换失败: {ex.Message}");
            _suppressLanToggle = true;
            LanEnabledToggle.IsOn = App.BackendServer.IsLanEnabled;
            _suppressLanToggle = false;
            CopyPairingButton.IsEnabled = LanEnabledToggle.IsOn;
        }
    }

    private async Task GeneratePairingAsync()
    {
        try
        {
            var baseUri = App.BackendServer.HttpBaseUri;
            if (baseUri is null)
            {
                SetStatus("后端未就绪");
                return;
            }

            if (!App.BackendServer.IsLanEnabled)
            {
                SetStatus("请先启用局域网共享");
                return;
            }

            var uri = new Uri(baseUri, "api/v1/connections/pairings");
            var payload = JsonSerializer.Serialize(new { expiresInSeconds = 300 }, JsonOptions);
            using var content = new StringContent(payload, Utf8NoBom, "application/json");

            SetStatus("生成配对邀请码…");
            using var response = await _httpClient.PostAsync(uri, content, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("pairingCode", out var codeProp))
            {
                SetStatus("生成失败: 响应缺少 pairingCode");
                return;
            }

            _currentPairingCode = codeProp.GetString();
            await UpdatePairingUiAsync();
            SetStatus("已生成二维码，等待扫码");
        }
        catch (Exception ex)
        {
            SetStatus($"生成失败: {ex.Message}");
        }
    }

    private async Task UpdatePairingUiAsync()
    {
        var baseUri = App.BackendServer.HttpBaseUri;
        if (baseUri is null)
        {
            return;
        }

        var ip = LanAddressSelector.TryGetPreferredLanIpv4Address();
        if (string.IsNullOrWhiteSpace(ip))
        {
            SetStatus("未检测到可用局域网 IPv4");
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentPairingCode))
        {
            return;
        }

        var port = baseUri.Port;
        var lanBaseUrl = $"http://{ip}:{port}/";
        _currentPairingUri = $"codex-bridge://pair?baseUrl={Uri.EscapeDataString(lanBaseUrl)}&pairingCode={Uri.EscapeDataString(_currentPairingCode)}";

        try
        {
            var qrGenerator = new QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(_currentPairingUri, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new PngByteQRCode(qrData);
            var pngBytes = qrCode.GetGraphic(20);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngBytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            QrImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            SetStatus($"二维码渲染失败: {ex.Message}");
        }
    }

    private void ClearPairingUi()
    {
        _currentPairingCode = null;
        _currentPairingUri = null;
        QrImage.Source = null;
    }

    private void CopyPairingButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPairingUri))
        {
            SetStatus("请先启用局域网共享");
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(_currentPairingUri);
        Clipboard.SetContent(dataPackage);
        SetStatus("配对链接已复制到剪贴板");
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var baseUri = App.BackendServer.HttpBaseUri;
            if (baseUri is null)
            {
                SetStatus("后端未就绪");
                return;
            }

            var uri = new Uri(baseUri, "api/v1/connections/devices");
            SetStatus("加载设备…");
            using var response = await _httpClient.GetAsync(uri, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var items = JsonSerializer.Deserialize<PairedDevice[]>(json, JsonOptions) ?? Array.Empty<PairedDevice>();

            Devices.Clear();
            foreach (var item in items.OrderByDescending(d => d.Online).ThenByDescending(d => d.LastSeenAt ?? d.CreatedAt))
            {
                Devices.Add(new PairedDeviceViewModel(item));
            }

            SetStatus($"已加载 {Devices.Count} 台设备");
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}");
        }
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string deviceId || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "撤销设备访问？",
            Content = "撤销后该设备将立即断开连接，并且后续无法访问（可重新配对）。",
            PrimaryButtonText = "撤销",
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

            var uri = new Uri(baseUri, $"api/v1/connections/devices/{deviceId}");
            SetStatus("撤销中…");
            using var response = await _httpClient.DeleteAsync(uri, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            await RefreshDevicesAsync();
            SetStatus("已撤销设备");
        }
        catch (Exception ex)
        {
            SetStatus($"撤销失败: {ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
    }
}
