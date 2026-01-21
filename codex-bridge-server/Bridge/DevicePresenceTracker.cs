// DevicePresenceTracker：跟踪已授权设备的在线状态（基于 WebSocket 连接）。
using System.Collections.Concurrent;

namespace codex_bridge_server.Bridge;

public sealed class DevicePresenceTracker
{
    private readonly ConcurrentDictionary<string, string?> _clientToDeviceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _deviceConnectionCount = new(StringComparer.OrdinalIgnoreCase);

    public void TrackClient(string clientId, string? deviceId)
    {
        _clientToDeviceId[clientId] = deviceId;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _deviceConnectionCount.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
    }

    public void UntrackClient(string clientId)
    {
        if (!_clientToDeviceId.TryRemove(clientId, out var deviceId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var newCount = _deviceConnectionCount.AddOrUpdate(deviceId, 0, (_, count) => Math.Max(0, count - 1));
        if (newCount <= 0)
        {
            _deviceConnectionCount.TryRemove(deviceId, out _);
        }
    }

    public bool IsOnline(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) && _deviceConnectionCount.ContainsKey(deviceId);
}

