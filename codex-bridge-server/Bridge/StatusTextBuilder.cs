using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace codex_bridge_server.Bridge;

public sealed class StatusTextBuilder
{
    private const string Unavailable = "不可用";

    private readonly CodexSessionStore _sessionStore;

    public StatusTextBuilder(CodexSessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    public string Build(StatusTextRequest request)
    {
        var sessionId = Normalize(request.SessionId);

        var sessionFilePath = !string.IsNullOrWhiteSpace(sessionId)
            ? _sessionStore.TryGetSessionFilePath(sessionId)
            : null;

        var snapshot = TryReadTokenCountSnapshot(sessionFilePath);

        var sb = new StringBuilder();
        sb.AppendLine($"5h限额: {FormatRateLimit(snapshot?.FiveHour)}");
        sb.AppendLine($"周限额: {FormatRateLimit(snapshot?.Weekly)}");
        sb.AppendLine($"上下文用量: {FormatContextUsagePercent(snapshot?.ContextUsagePercent)}");
        return sb.ToString();
    }

    private static string? Normalize(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string FormatRateLimit(RateLimitWindow? window)
    {
        if (window is null)
        {
            return Unavailable;
        }

        var used = window.UsedPercent is null ? Unavailable : $"{window.UsedPercent:0.#}%";
        var resetsAt = window.ResetsAtLocal is null ? Unavailable : window.ResetsAtLocal.Value.ToString("MM-dd HH:mm");
        return $"已用 {used}，重置 {resetsAt}";
    }

    private static string FormatContextUsagePercent(int? percent)
    {
        if (percent is null)
        {
            return Unavailable;
        }

        var clamped = Math.Clamp(percent.Value, 0, 100);
        return $"{clamped}%";
    }

    private static TokenCountSnapshot? TryReadTokenCountSnapshot(string? sessionFilePath)
    {
        var (fiveHour, weekly) = TryReadRateLimits(sessionFilePath);
        var contextUsagePercent = TryReadContextUsagePercent(sessionFilePath);

        if (fiveHour is null && weekly is null && contextUsagePercent is null)
        {
            return null;
        }

        return new TokenCountSnapshot
        {
            FiveHour = fiveHour,
            Weekly = weekly,
            ContextUsagePercent = contextUsagePercent,
        };
    }

    private static (RateLimitWindow? fiveHour, RateLimitWindow? weekly) TryReadRateLimits(string? sessionFilePath)
    {
        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
        {
            return (null, null);
        }

        try
        {
            var tail = ReadTailText(sessionFilePath, maxBytes: 512 * 1024);
            if (string.IsNullOrWhiteSpace(tail))
            {
                return (null, null);
            }

            var lines = tail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseJson(line, out var root))
                {
                    continue;
                }

                if (!TryGetString(root, "type", out var type) || !string.Equals(type, "event_msg", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "token_count", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                RateLimitWindow? fiveHour = null;
                RateLimitWindow? weekly = null;

                if (rateLimits.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
                {
                    var parsed = TryParseRateLimitWindow(primary);
                    if (parsed?.WindowMinutes == 300)
                    {
                        fiveHour = parsed;
                    }
                    else if (parsed?.WindowMinutes == 10080)
                    {
                        weekly = parsed;
                    }
                }

                if (rateLimits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                {
                    var parsed = TryParseRateLimitWindow(secondary);
                    if (parsed?.WindowMinutes == 300)
                    {
                        fiveHour ??= parsed;
                    }
                    else if (parsed?.WindowMinutes == 10080)
                    {
                        weekly ??= parsed;
                    }
                }

                return (fiveHour, weekly);
            }
        }
        catch
        {
        }

        return (null, null);
    }

    private static int? TryReadContextUsagePercent(string? sessionFilePath)
    {
        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
        {
            return null;
        }

        try
        {
            var tail = ReadTailText(sessionFilePath, maxBytes: 512 * 1024);
            if (string.IsNullOrWhiteSpace(tail))
            {
                return null;
            }

            var lines = tail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseJson(line, out var root))
                {
                    continue;
                }

                if (!TryGetString(root, "type", out var type) || !string.Equals(type, "event_msg", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetString(payload, "type", out var payloadType) || !string.Equals(payloadType, "token_count", StringComparison.Ordinal))
                {
                    continue;
                }

                return TryComputeContextUsagePercent(payload);
            }
        }
        catch
        {
        }

        return null;
    }

    private static int? TryComputeContextUsagePercent(JsonElement tokenCountPayload)
    {
        if (!tokenCountPayload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetInt64(info, "model_context_window", out var contextWindow) || contextWindow <= 0)
        {
            return null;
        }

        if (!info.TryGetProperty("last_token_usage", out var lastUsage) || lastUsage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetInt64(lastUsage, "input_tokens", out var inputTokens) || inputTokens < 0)
        {
            return null;
        }

        var percent = (int)Math.Round((double)inputTokens / contextWindow * 100, MidpointRounding.AwayFromZero);
        return Math.Clamp(percent, 0, 100);
    }

    private static RateLimitWindow? TryParseRateLimitWindow(JsonElement obj)
    {
        if (!TryGetDouble(obj, "used_percent", out var usedPercent)
            || !TryGetInt32(obj, "window_minutes", out var windowMinutes)
            || !TryGetInt64(obj, "resets_at", out var resetsAtSeconds))
        {
            return null;
        }

        DateTimeOffset? resetsAtLocal = null;
        try
        {
            resetsAtLocal = DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds).ToLocalTime();
        }
        catch
        {
        }

        return new RateLimitWindow
        {
            UsedPercent = usedPercent,
            WindowMinutes = windowMinutes,
            ResetsAtLocal = resetsAtLocal,
        };
    }

    private static string? ReadTailText(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var offset = Math.Max(0, length - maxBytes);
        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static bool TryParseJson(string json, out JsonElement root)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!obj.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetDouble(JsonElement obj, string propertyName, out double value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetInt32(JsonElement obj, string propertyName, out int value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetInt64(JsonElement obj, string propertyName, out long value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return false;
    }

    public sealed class StatusTextRequest
    {
        public string? SessionId { get; init; }
    }

    private sealed class TokenCountSnapshot
    {
        public RateLimitWindow? FiveHour { get; init; }
        public RateLimitWindow? Weekly { get; init; }
        public int? ContextUsagePercent { get; init; }
    }

    private sealed class RateLimitWindow
    {
        public double? UsedPercent { get; init; }
        public int? WindowMinutes { get; init; }
        public DateTimeOffset? ResetsAtLocal { get; init; }
    }
}
