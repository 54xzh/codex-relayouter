using codex_bridge.Networking;
using System.Net;
using System.Net.NetworkInformation;

namespace codex_bridge_common.Tests;

public sealed class LanAddressSelectorTests
{
    [Fact]
    public void SelectPreferredLanIpv4Address_PrefersPrivateWithGatewayOverVirtual()
    {
        var candidates = new[]
        {
            new LanAddressSelector.LanIpCandidate(
                IPAddress.Parse("192.168.146.1"),
                HasIpv4Gateway: true,
                NetworkInterfaceType.Ethernet,
                InterfaceName: "vEthernet (Default Switch)",
                InterfaceDescription: "Hyper-V Virtual Ethernet Adapter"),
            new LanAddressSelector.LanIpCandidate(
                IPAddress.Parse("192.168.1.15"),
                HasIpv4Gateway: true,
                NetworkInterfaceType.Wireless80211,
                InterfaceName: "Wi-Fi",
                InterfaceDescription: "Intel Wi-Fi 6"),
        };

        var selected = LanAddressSelector.SelectPreferredLanIpv4Address(candidates);

        Assert.Equal(IPAddress.Parse("192.168.1.15"), selected);
    }

    [Fact]
    public void SelectPreferredLanIpv4Address_AvoidsBenchmarkNetwork()
    {
        var candidates = new[]
        {
            new LanAddressSelector.LanIpCandidate(
                IPAddress.Parse("198.18.0.1"),
                HasIpv4Gateway: true,
                NetworkInterfaceType.Ethernet,
                InterfaceName: "bench",
                InterfaceDescription: "Benchmark Adapter"),
            new LanAddressSelector.LanIpCandidate(
                IPAddress.Parse("10.0.0.5"),
                HasIpv4Gateway: false,
                NetworkInterfaceType.Ethernet,
                InterfaceName: "Ethernet",
                InterfaceDescription: "Realtek"),
        };

        var selected = LanAddressSelector.SelectPreferredLanIpv4Address(candidates);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), selected);
    }

    [Fact]
    public void SelectPreferredLanIpv4Address_ReturnsNullWhenEmpty()
    {
        var selected = LanAddressSelector.SelectPreferredLanIpv4Address(Array.Empty<LanAddressSelector.LanIpCandidate>());
        Assert.Null(selected);
    }
}

