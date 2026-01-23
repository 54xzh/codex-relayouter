// ConnectionsController：提供配对邀请码、配对审批、设备列表与撤销等接口（用于局域网设备连接管理）。
using codex_bridge_server.Bridge;
using Microsoft.AspNetCore.Mvc;

namespace codex_bridge_server.Controllers;

[ApiController]
[Route("api/v1/connections")]
public sealed class ConnectionsController : ControllerBase
{
    private readonly BridgeRequestAuthorizer _authorizer;
    private readonly DevicePairingService _pairingService;
    private readonly PairedDeviceStore _deviceStore;
    private readonly DevicePresenceTracker _presenceTracker;
    private readonly WebSocketHub _hub;

    public ConnectionsController(
        BridgeRequestAuthorizer authorizer,
        DevicePairingService pairingService,
        PairedDeviceStore deviceStore,
        DevicePresenceTracker presenceTracker,
        WebSocketHub hub)
    {
        _authorizer = authorizer;
        _pairingService = pairingService;
        _deviceStore = deviceStore;
        _presenceTracker = presenceTracker;
        _hub = hub;
    }

    [HttpPost("pairings")]
    public IActionResult CreatePairingCode([FromBody] PairingCodeCreateRequest? request)
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        TimeSpan? expiresIn = request?.ExpiresInSeconds is int seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        var code = _pairingService.CreatePairingCode(expiresIn);
        return Ok(new
        {
            pairingCode = code.PairingCodeValue,
            expiresAt = code.ExpiresAt,
        });
    }

    [HttpPost("pairings/claim")]
    public async Task<IActionResult> Claim([FromBody] PairingClaimRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "请求体不能为空" });
        }

        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var claim = _pairingService.Claim(request, clientIp);

            if (_pairingService.TryGetPendingRequest(claim.RequestId, out var notification))
            {
                await _hub.NotifyPairingRequestedAsync(notification, cancellationToken);
            }

            return Ok(new
            {
                requestId = claim.RequestId,
                pollAfterMs = 800,
                expiresAt = claim.ExpiresAt,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("pairings/{requestId}")]
    public IActionResult Poll([FromRoute] string requestId)
    {
        var result = _pairingService.Poll(requestId);
        return Ok(new
        {
            status = result.Status,
            deviceId = result.DeviceId,
            deviceToken = result.DeviceToken,
            tokenDelivered = result.TokenDelivered,
            message = result.Message,
        });
    }

    [HttpPost("pairings/{requestId}/respond")]
    public IActionResult Respond([FromRoute] string requestId, [FromBody] PairingRespondRequest? request)
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        var decision = request?.Decision?.Trim();
        var parsed = string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase)
            ? PairingDecision.Approve
            : string.Equals(decision, "decline", StringComparison.OrdinalIgnoreCase)
                ? PairingDecision.Decline
                : (PairingDecision?)null;

        if (parsed is null)
        {
            return BadRequest(new { message = "decision 必须为 approve/decline" });
        }

        var result = _pairingService.Respond(requestId, parsed.Value);
        if (string.Equals(result.Status, "notFound", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        return Ok(new { status = result.Status, deviceId = result.DeviceId });
    }

    [HttpGet("devices")]
    public IActionResult ListDevices()
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        var devices = _deviceStore.ListDevices()
            .Select(d => new
            {
                deviceId = d.DeviceId,
                name = d.Name,
                platform = d.Platform,
                deviceModel = d.DeviceModel,
                createdAt = d.CreatedAt,
                lastSeenAt = d.LastSeenAt,
                revoked = d.RevokedAt is not null,
                revokedAt = d.RevokedAt,
                online = _presenceTracker.IsOnline(d.DeviceId),
            })
            .ToArray();

        return Ok(devices);
    }

    [HttpDelete("devices/{deviceId}")]
    public async Task<IActionResult> Revoke([FromRoute] string deviceId, CancellationToken cancellationToken)
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        var success = _deviceStore.RevokeDevice(deviceId);
        if (!success)
        {
            return NotFound();
        }

        await _hub.DisconnectDeviceAsync(deviceId, cancellationToken);
        return Ok(new { deviceId, revoked = true });
    }

    public sealed class PairingCodeCreateRequest
    {
        public int? ExpiresInSeconds { get; set; }
    }

    public sealed class PairingRespondRequest
    {
        public string? Decision { get; set; }
    }
}
