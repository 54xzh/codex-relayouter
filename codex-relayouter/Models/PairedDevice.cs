// PairedDevice：连接页的设备展示模型（来自 Bridge Server /api/v1/connections/devices）。
using System;

namespace codex_bridge.Models;

public sealed class PairedDevice
{
    public required string DeviceId { get; set; }

    public string? Name { get; set; }

    public string? Platform { get; set; }

    public string? DeviceModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public bool Revoked { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool Online { get; set; }
}

