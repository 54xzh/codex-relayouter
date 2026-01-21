// PairedDeviceViewModel：连接页设备列表的展示模型。
using codex_bridge.Models;
using System;

namespace codex_bridge.ViewModels;

public sealed class PairedDeviceViewModel
{
    public string DeviceId { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string StatusText { get; }
    public bool IsOnline { get; }
    public bool IsRevoked { get; }
    public bool CanRevoke => !IsRevoked;

    public PairedDeviceViewModel(PairedDevice device)
    {
        DeviceId = device.DeviceId;
        Title = string.IsNullOrWhiteSpace(device.Name) ? device.DeviceId : device.Name.Trim();

        var platform = string.IsNullOrWhiteSpace(device.Platform) ? "unknown" : device.Platform.Trim();
        var model = string.IsNullOrWhiteSpace(device.DeviceModel) ? null : device.DeviceModel.Trim();
        Subtitle = string.IsNullOrWhiteSpace(model) ? platform : $"{platform} · {model}";

        IsOnline = device.Online;
        IsRevoked = device.Revoked;

        var seen = device.LastSeenAt is null ? "未知" : device.LastSeenAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        var onlineText = device.Online ? "在线" : "离线";
        var revokedText = device.Revoked ? "（已撤销）" : string.Empty;
        StatusText = $"状态: {onlineText}{revokedText} · 最近活动: {seen}";
    }
}
