using codex_bridge_server.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace codex_bridge_server.Tests;

public sealed class DevicePairingAndAuthTests
{
    [Fact]
    public void Authorizer_RemoteDisabled_AllowsLoopbackOnly()
    {
        var store = new PairedDeviceStore(NullLogger<PairedDeviceStore>.Instance, filePath: GetTempFilePath());
        var options = Options.Create(new BridgeSecurityOptions { RemoteEnabled = false, BearerToken = string.Empty });
        var authorizer = new BridgeRequestAuthorizer(options, store);

        var loopback = new DefaultHttpContext();
        loopback.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(authorizer.IsAuthorized(loopback));
        Assert.True(authorizer.IsManagementAuthorized(loopback));

        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        Assert.False(authorizer.IsAuthorized(remote));
        Assert.False(authorizer.IsManagementAuthorized(remote));
    }

    [Fact]
    public void Authorizer_RemoteEnabled_AllowsDeviceToken()
    {
        var store = new PairedDeviceStore(NullLogger<PairedDeviceStore>.Instance, filePath: GetTempFilePath());
        var registration = store.RegisterDevice(new PairedDeviceRegistrationRequest("Pixel", "android", "Pixel 8"));

        var options = Options.Create(new BridgeSecurityOptions { RemoteEnabled = true, BearerToken = string.Empty });
        var authorizer = new BridgeRequestAuthorizer(options, store);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.60");
        ctx.Request.Headers.Authorization = $"Bearer {registration.DeviceToken}";

        var auth = authorizer.Authorize(ctx);
        Assert.True(auth.IsAuthorized);
        Assert.False(auth.IsLoopback);
        Assert.Equal(registration.DeviceId, auth.DeviceId);
        Assert.False(authorizer.IsManagementAuthorized(ctx));
    }

    [Fact]
    public void PairingService_Approve_FlowsAndDeliversTokenOnce()
    {
        var store = new PairedDeviceStore(NullLogger<PairedDeviceStore>.Instance, filePath: GetTempFilePath());
        var options = Options.Create(new BridgeSecurityOptions { RemoteEnabled = true, BearerToken = string.Empty });
        var pairing = new DevicePairingService(options, store, NullLogger<DevicePairingService>.Instance);

        var code = pairing.CreatePairingCode();
        var claim = pairing.Claim(
            new PairingClaimRequest(
                PairingCode: code.PairingCodeValue,
                DeviceName: "Pixel",
                Platform: "android",
                DeviceModel: "Pixel 8",
                AppVersion: "1.0"),
            clientIp: "192.168.1.70");

        var before = pairing.Poll(claim.RequestId);
        Assert.Equal("pending", before.Status);

        var respond = pairing.Respond(claim.RequestId, PairingDecision.Approve);
        Assert.Equal("approved", respond.Status);
        Assert.False(string.IsNullOrWhiteSpace(respond.DeviceId));

        var first = pairing.Poll(claim.RequestId);
        Assert.Equal("approved", first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceToken));
        Assert.False(first.TokenDelivered);

        var second = pairing.Poll(claim.RequestId);
        Assert.Equal("approved", second.Status);
        Assert.False(string.IsNullOrWhiteSpace(second.DeviceId));
        Assert.True(string.IsNullOrWhiteSpace(second.DeviceToken));
        Assert.True(second.TokenDelivered);
    }

    [Fact]
    public void PairingService_Claim_WhenRemoteDisabled_Fails()
    {
        var store = new PairedDeviceStore(NullLogger<PairedDeviceStore>.Instance, filePath: GetTempFilePath());
        var options = Options.Create(new BridgeSecurityOptions { RemoteEnabled = false, BearerToken = string.Empty });
        var pairing = new DevicePairingService(options, store, NullLogger<DevicePairingService>.Instance);

        var code = pairing.CreatePairingCode();
        Assert.Throws<InvalidOperationException>(() =>
            pairing.Claim(
                new PairingClaimRequest(code.PairingCodeValue, "Pixel", "android", "Pixel 8", "1.0"),
                clientIp: "192.168.1.70"));
    }

    private static string GetTempFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "paired-devices.json");
    }
}

