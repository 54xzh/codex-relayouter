// PairedDeviceStore：管理已配对设备与设备令牌（仅存储哈希），并提供撤销与 lastSeen 更新。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace codex_bridge_server.Bridge;

public sealed class PairedDeviceStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILogger<PairedDeviceStore> _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private PairedDevicesFile _file = new();
    private DateTimeOffset _lastSavedAt;

    // lastSeen 频率控制：避免每个请求都落盘
    private static readonly TimeSpan LastSeenWriteThrottle = TimeSpan.FromSeconds(10);

    public PairedDeviceStore(ILogger<PairedDeviceStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;
        Load();
    }

    public IReadOnlyList<PairedDeviceInfo> ListDevices()
    {
        lock (_gate)
        {
            return _file.Devices
                .Select(d => new PairedDeviceInfo(
                    DeviceId: d.DeviceId,
                    Name: d.Name,
                    Platform: d.Platform,
                    DeviceModel: d.DeviceModel,
                    CreatedAt: d.CreatedAt,
                    LastSeenAt: d.LastSeenAt,
                    RevokedAt: d.RevokedAt))
                .ToArray();
        }
    }

    public bool TryAuthorizeDeviceToken(string token, out string deviceId)
    {
        deviceId = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        PairedDeviceEntry? device;
        lock (_gate)
        {
            device = _file.Devices.FirstOrDefault(d => d.RevokedAt is null && DeviceTokenHasher.VerifyBase64Hash(token, d.TokenHash));
        }

        if (device is null)
        {
            return false;
        }

        deviceId = device.DeviceId;
        Touch(deviceId);
        return true;
    }

    public PairedDeviceRegistration RegisterDevice(PairedDeviceRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("deviceName 不能为空", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Platform))
        {
            throw new ArgumentException("platform 不能为空", nameof(request));
        }

        var token = GenerateToken();
        var tokenHash = DeviceTokenHasher.ComputeBase64Hash(token);

        var now = DateTimeOffset.UtcNow;
        var device = new PairedDeviceEntry
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            Platform = request.Platform.Trim(),
            DeviceModel = string.IsNullOrWhiteSpace(request.DeviceModel) ? null : request.DeviceModel.Trim(),
            CreatedAt = now,
            LastSeenAt = now,
            RevokedAt = null,
            TokenHash = tokenHash,
        };

        lock (_gate)
        {
            _file.Devices.Add(device);
            Save();
        }

        return new PairedDeviceRegistration(
            DeviceId: device.DeviceId,
            DeviceToken: token);
    }

    public bool RevokeDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_gate)
        {
            var target = _file.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            if (target.RevokedAt is not null)
            {
                return true;
            }

            target.RevokedAt = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    public void Touch(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var target = _file.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null || target.RevokedAt is not null)
            {
                return;
            }

            if (target.LastSeenAt is not null && now - target.LastSeenAt.Value < LastSeenWriteThrottle)
            {
                return;
            }

            target.LastSeenAt = now;

            // 避免高频落盘：同样做一次节流
            if (now - _lastSavedAt < LastSeenWriteThrottle)
            {
                return;
            }

            Save();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _file = new PairedDevicesFile();
                    return;
                }

                var json = File.ReadAllText(_filePath, Utf8NoBom);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _file = new PairedDevicesFile();
                    return;
                }

                var parsed = JsonSerializer.Deserialize<PairedDevicesFile>(json, JsonOptions);
                _file = parsed ?? new PairedDevicesFile();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取已配对设备列表失败，将使用空列表: {Path}", _filePath);
                _file = new PairedDevicesFile();
            }
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_file, JsonOptions);
            File.WriteAllText(_filePath, json, Utf8NoBom);
            _lastSavedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存已配对设备列表失败: {Path}", _filePath);
        }
    }

    private static string GetDefaultFilePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        }

        return Path.Combine(baseDir, "codex-bridge", "paired-devices.json");
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        // Base64Url：更适合二维码/复制粘贴
        var base64 = Convert.ToBase64String(bytes);
        return base64
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class PairedDevicesFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("devices")]
        public List<PairedDeviceEntry> Devices { get; set; } = new();
    }

    private sealed class PairedDeviceEntry
    {
        [JsonPropertyName("deviceId")]
        public required string DeviceId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("platform")]
        public required string Platform { get; init; }

        [JsonPropertyName("deviceModel")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeviceModel { get; init; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("lastSeenAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LastSeenAt { get; set; }

        [JsonPropertyName("revokedAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RevokedAt { get; set; }

        [JsonPropertyName("tokenHash")]
        public required string TokenHash { get; init; }
    }
}

public sealed record PairedDeviceInfo(
    string DeviceId,
    string Name,
    string Platform,
    string? DeviceModel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt);

public sealed record PairedDeviceRegistrationRequest(
    string Name,
    string Platform,
    string? DeviceModel);

public sealed record PairedDeviceRegistration(
    string DeviceId,
    string DeviceToken);

