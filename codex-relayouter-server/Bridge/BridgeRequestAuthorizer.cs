// BridgeRequestAuthorizer：复用 WS/HTTP 的统一鉴权逻辑（默认仅回环；启用远程后使用“设备令牌”鉴权，支持逐设备撤销）。
using System.Net;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class BridgeRequestAuthorizer
{
    private readonly IOptions<BridgeSecurityOptions> _securityOptions;
    private readonly PairedDeviceStore _deviceStore;

    private const string AuthContextKey = "codex_bridge_server.Bridge.AuthResult";

    public BridgeRequestAuthorizer(IOptions<BridgeSecurityOptions> securityOptions, PairedDeviceStore deviceStore)
    {
        _securityOptions = securityOptions;
        _deviceStore = deviceStore;
    }

    public bool IsAuthorized(HttpContext context)
    {
        return Authorize(context).IsAuthorized;
    }

    public bool IsManagementAuthorized(HttpContext context) =>
        Authorize(context).IsLoopback;

    public BridgeAuthorizationResult Authorize(HttpContext context)
    {
        if (context.Items.TryGetValue(AuthContextKey, out var cached) && cached is BridgeAuthorizationResult cachedResult)
        {
            return cachedResult;
        }

        var options = _securityOptions.Value;
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is not null && IPAddress.IsLoopback(remoteIp);

        BridgeAuthorizationResult result;
        if (!options.RemoteEnabled)
        {
            result = isLoopback
                ? new BridgeAuthorizationResult(IsAuthorized: true, IsLoopback: true, DeviceId: null)
                : new BridgeAuthorizationResult(IsAuthorized: false, IsLoopback: false, DeviceId: null);
        }
        else if (isLoopback)
        {
            result = new BridgeAuthorizationResult(IsAuthorized: true, IsLoopback: true, DeviceId: null);
        }
        else
        {
            var token = TryGetBearerToken(context);
            if (!string.IsNullOrWhiteSpace(token) && _deviceStore.TryAuthorizeDeviceToken(token, out var deviceId))
            {
                result = new BridgeAuthorizationResult(IsAuthorized: true, IsLoopback: false, DeviceId: deviceId);
            }
            else
            {
                // 兼容旧配置：如果显式设置 BearerToken，则允许其作为“全局令牌”使用。
                // 说明：MVP 推荐使用设备令牌；全局令牌用于调试/兼容旧客户端。
                var legacy = options.BearerToken;
                if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(legacy)
                    && string.Equals(token, legacy, StringComparison.Ordinal))
                {
                    result = new BridgeAuthorizationResult(IsAuthorized: true, IsLoopback: false, DeviceId: null);
                }
                else
                {
                    result = new BridgeAuthorizationResult(IsAuthorized: false, IsLoopback: false, DeviceId: null);
                }
            }
        }

        context.Items[AuthContextKey] = result;
        return result;
    }

    private static string? TryGetBearerToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authHeader[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}

public readonly record struct BridgeAuthorizationResult(bool IsAuthorized, bool IsLoopback, string? DeviceId);
