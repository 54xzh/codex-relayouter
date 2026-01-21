// DevicePairingService：处理配对邀请码、配对请求、审批与设备令牌签发（用于局域网首连确认）。
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace codex_bridge_server.Bridge;

public sealed class DevicePairingService
{
    private readonly IOptions<BridgeSecurityOptions> _securityOptions;
    private readonly PairedDeviceStore _deviceStore;
    private readonly ILogger<DevicePairingService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, PairingCodeEntry> _pairingCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PairingRequestEntry> _requests = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan DefaultPairingCodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultPairingRequestLifetime = TimeSpan.FromMinutes(10);

    public DevicePairingService(
        IOptions<BridgeSecurityOptions> securityOptions,
        PairedDeviceStore deviceStore,
        ILogger<DevicePairingService> logger)
    {
        _securityOptions = securityOptions;
        _deviceStore = deviceStore;
        _logger = logger;
    }

    public PairingCode CreatePairingCode(TimeSpan? expiresIn = null)
    {
        var lifetime = expiresIn ?? DefaultPairingCodeLifetime;
        lifetime = lifetime <= TimeSpan.Zero ? DefaultPairingCodeLifetime : lifetime;
        if (lifetime > TimeSpan.FromMinutes(30))
        {
            lifetime = TimeSpan.FromMinutes(30);
        }

        var now = DateTimeOffset.UtcNow;
        var code = GenerateCode();
        var expiresAt = now.Add(lifetime);

        lock (_gate)
        {
            CleanupExpiredLocked(now);
            _pairingCodes[code] = new PairingCodeEntry(ExpiresAt: expiresAt);
        }

        return new PairingCode(code, expiresAt);
    }

    public PairingClaimResult Claim(PairingClaimRequest request, string? clientIp)
    {
        if (!_securityOptions.Value.RemoteEnabled)
        {
            throw new InvalidOperationException("远程访问未启用");
        }

        if (string.IsNullOrWhiteSpace(request.PairingCode))
        {
            throw new ArgumentException("pairingCode 不能为空", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            throw new ArgumentException("deviceName 不能为空", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Platform))
        {
            throw new ArgumentException("platform 不能为空", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var pairingCode = request.PairingCode.Trim();

        PairingCodeEntry? codeEntry;
        lock (_gate)
        {
            CleanupExpiredLocked(now);

            if (!_pairingCodes.TryGetValue(pairingCode, out codeEntry) || codeEntry is null || codeEntry.ExpiresAt <= now)
            {
                throw new InvalidOperationException("pairingCode 无效或已过期");
            }

            // 一次性邀请码：使用后即失效
            _pairingCodes.Remove(pairingCode);

            var requestId = Guid.NewGuid().ToString("N");
            var expiresAt = now.Add(DefaultPairingRequestLifetime);

            _requests[requestId] = new PairingRequestEntry
            {
                RequestId = requestId,
                CreatedAt = now,
                ExpiresAt = expiresAt,
                ClientIp = string.IsNullOrWhiteSpace(clientIp) ? null : clientIp.Trim(),
                DeviceName = request.DeviceName.Trim(),
                Platform = request.Platform.Trim(),
                DeviceModel = string.IsNullOrWhiteSpace(request.DeviceModel) ? null : request.DeviceModel.Trim(),
                AppVersion = string.IsNullOrWhiteSpace(request.AppVersion) ? null : request.AppVersion.Trim(),
                Status = PairingRequestStatus.Pending,
            };

            return new PairingClaimResult(
                RequestId: requestId,
                ExpiresAt: expiresAt,
                DeviceName: request.DeviceName.Trim(),
                Platform: request.Platform.Trim(),
                DeviceModel: string.IsNullOrWhiteSpace(request.DeviceModel) ? null : request.DeviceModel.Trim(),
                ClientIp: string.IsNullOrWhiteSpace(clientIp) ? null : clientIp.Trim());
        }
    }

    public PairingPollResult Poll(string requestId)
    {
        if (!_securityOptions.Value.RemoteEnabled)
        {
            return PairingPollResult.RemoteDisabled();
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return PairingPollResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            CleanupExpiredLocked(now);

            if (!_requests.TryGetValue(requestId.Trim(), out var entry))
            {
                return PairingPollResult.NotFound();
            }

            if (entry.Status == PairingRequestStatus.Pending && entry.ExpiresAt <= now)
            {
                entry.Status = PairingRequestStatus.Expired;
                return PairingPollResult.Expired();
            }

            return entry.Status switch
            {
                PairingRequestStatus.Pending => PairingPollResult.Pending(),
                PairingRequestStatus.Declined => PairingPollResult.Declined(),
                PairingRequestStatus.Expired => PairingPollResult.Expired(),
                PairingRequestStatus.Approved => BuildApprovedPollResult(entry),
                _ => PairingPollResult.NotFound(),
            };
        }
    }

    public PairingRespondResult Respond(string requestId, PairingDecision decision)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return PairingRespondResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            CleanupExpiredLocked(now);

            if (!_requests.TryGetValue(requestId.Trim(), out var entry))
            {
                return PairingRespondResult.NotFound();
            }

            if (entry.Status != PairingRequestStatus.Pending)
            {
                return new PairingRespondResult(entry.Status.ToWire(), entry.DeviceId);
            }

            if (entry.ExpiresAt <= now)
            {
                entry.Status = PairingRequestStatus.Expired;
                return new PairingRespondResult("expired", null);
            }

            if (decision == PairingDecision.Decline)
            {
                entry.Status = PairingRequestStatus.Declined;
                return new PairingRespondResult("declined", null);
            }

            try
            {
                var registration = _deviceStore.RegisterDevice(new PairedDeviceRegistrationRequest(
                    Name: entry.DeviceName,
                    Platform: entry.Platform,
                    DeviceModel: entry.DeviceModel));

                entry.Status = PairingRequestStatus.Approved;
                entry.DeviceId = registration.DeviceId;
                entry.DeviceToken = registration.DeviceToken;
                entry.TokenDelivered = false;

                return new PairingRespondResult("approved", registration.DeviceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "签发设备令牌失败: {RequestId}", requestId);
                entry.Status = PairingRequestStatus.Declined;
                return new PairingRespondResult("declined", null);
            }
        }
    }

    public bool TryGetPendingRequest(string requestId, out PairingRequestNotification notification)
    {
        notification = null!;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            CleanupExpiredLocked(now);

            if (!_requests.TryGetValue(requestId.Trim(), out var entry))
            {
                return false;
            }

            if (entry.Status != PairingRequestStatus.Pending)
            {
                return false;
            }

            notification = new PairingRequestNotification(
                RequestId: entry.RequestId,
                DeviceName: entry.DeviceName,
                Platform: entry.Platform,
                DeviceModel: entry.DeviceModel,
                AppVersion: entry.AppVersion,
                ClientIp: entry.ClientIp,
                ExpiresAt: entry.ExpiresAt);
            return true;
        }
    }

    private static PairingPollResult BuildApprovedPollResult(PairingRequestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DeviceId))
        {
            return PairingPollResult.Declined();
        }

        if (entry.TokenDelivered)
        {
            return PairingPollResult.Approved(deviceId: entry.DeviceId, deviceToken: null, tokenDelivered: true);
        }

        entry.TokenDelivered = true;
        return PairingPollResult.Approved(deviceId: entry.DeviceId, deviceToken: entry.DeviceToken, tokenDelivered: false);
    }

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        if (_pairingCodes.Count > 0)
        {
            var expiredCodes = _pairingCodes
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToArray();
            foreach (var code in expiredCodes)
            {
                _pairingCodes.Remove(code);
            }
        }

        if (_requests.Count > 0)
        {
            var expiredRequests = _requests
                .Where(kvp => kvp.Value.ExpiresAt <= now && kvp.Value.Status == PairingRequestStatus.Pending)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var requestId in expiredRequests)
            {
                if (_requests.TryGetValue(requestId, out var entry) && entry.Status == PairingRequestStatus.Pending)
                {
                    entry.Status = PairingRequestStatus.Expired;
                }
            }
        }
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var base64 = Convert.ToBase64String(bytes);
        return base64
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed record PairingCodeEntry(DateTimeOffset ExpiresAt);

    private sealed class PairingRequestEntry
    {
        public required string RequestId { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required string DeviceName { get; init; }
        public required string Platform { get; init; }
        public string? DeviceModel { get; init; }
        public string? AppVersion { get; init; }
        public string? ClientIp { get; init; }

        public PairingRequestStatus Status { get; set; }

        public string? DeviceId { get; set; }
        public string? DeviceToken { get; set; }
        public bool TokenDelivered { get; set; }
    }
}

public sealed record PairingCode(string PairingCodeValue, DateTimeOffset ExpiresAt);

public sealed record PairingClaimRequest(
    string PairingCode,
    string DeviceName,
    string Platform,
    string? DeviceModel,
    string? AppVersion);

public sealed record PairingClaimResult(
    string RequestId,
    DateTimeOffset ExpiresAt,
    string DeviceName,
    string Platform,
    string? DeviceModel,
    string? ClientIp);

public sealed record PairingRequestNotification(
    string RequestId,
    string DeviceName,
    string Platform,
    string? DeviceModel,
    string? AppVersion,
    string? ClientIp,
    DateTimeOffset ExpiresAt);

public enum PairingDecision
{
    Approve = 1,
    Decline = 2,
}

internal enum PairingRequestStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Expired = 3,
}

internal static class PairingRequestStatusExtensions
{
    public static string ToWire(this PairingRequestStatus status) => status switch
    {
        PairingRequestStatus.Pending => "pending",
        PairingRequestStatus.Approved => "approved",
        PairingRequestStatus.Declined => "declined",
        PairingRequestStatus.Expired => "expired",
        _ => "unknown",
    };
}

public sealed record PairingRespondResult(string Status, string? DeviceId)
{
    public static PairingRespondResult NotFound() => new("notFound", null);
}

public sealed record PairingPollResult(string Status, string? DeviceId, string? DeviceToken, bool TokenDelivered, string? Message)
{
    public static PairingPollResult Pending() => new("pending", null, null, false, null);
    public static PairingPollResult Approved(string deviceId, string? deviceToken, bool tokenDelivered) => new("approved", deviceId, deviceToken, tokenDelivered, null);
    public static PairingPollResult Declined() => new("declined", null, null, false, null);
    public static PairingPollResult Expired() => new("expired", null, null, false, null);
    public static PairingPollResult NotFound() => new("notFound", null, null, false, null);
    public static PairingPollResult RemoteDisabled() => new("remoteDisabled", null, null, false, "远程访问未启用");
}
