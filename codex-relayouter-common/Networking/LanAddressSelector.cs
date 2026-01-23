using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace codex_bridge.Networking;

public static class LanAddressSelector
{
    public readonly record struct LanIpCandidate(
        IPAddress Address,
        bool HasIpv4Gateway,
        NetworkInterfaceType InterfaceType,
        string InterfaceName,
        string InterfaceDescription);

    public static IReadOnlyList<LanIpCandidate> GetLanIpv4Candidates()
    {
        var results = new List<LanIpCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties? props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var address = uni.Address;
                if (IPAddress.IsLoopback(address))
                {
                    continue;
                }

                var text = address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!seen.Add(text))
                {
                    continue;
                }

                results.Add(new LanIpCandidate(
                    address,
                    hasGateway,
                    nic.NetworkInterfaceType,
                    nic.Name ?? string.Empty,
                    nic.Description ?? string.Empty));
            }
        }

        return results;
    }

    public static string? TryGetPreferredLanIpv4Address()
        => SelectPreferredLanIpv4Address(GetLanIpv4Candidates())?.ToString();

    public static IPAddress? SelectPreferredLanIpv4Address(IEnumerable<LanIpCandidate> candidates)
    {
        var best = candidates
            .Select(c => (Candidate: c, Score: Score(c)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (best.Candidate.Address is null || best.Score <= int.MinValue / 2)
        {
            return null;
        }

        return best.Candidate.Address;
    }

    private static int Score(LanIpCandidate candidate)
    {
        var score = 0;

        if (IsBenchmarkNetwork(candidate.Address))
        {
            score -= 200;
        }

        if (IsPrivateIpv4(candidate.Address))
        {
            score += 100;
        }
        else if (IsCarrierGradeNat(candidate.Address))
        {
            score += 30;
        }

        if (candidate.HasIpv4Gateway)
        {
            score += 50;
        }

        score += candidate.InterfaceType switch
        {
            NetworkInterfaceType.Ethernet => 15,
            NetworkInterfaceType.GigabitEthernet => 15,
            NetworkInterfaceType.FastEthernetFx => 15,
            NetworkInterfaceType.FastEthernetT => 15,
            NetworkInterfaceType.Wireless80211 => 14,
            _ => 0
        };

        if (LooksVirtualAdapter(candidate.InterfaceName, candidate.InterfaceDescription))
        {
            score -= 40;
        }

        return score;
    }

    private static bool LooksVirtualAdapter(string name, string description)
    {
        var text = $"{name} {description}";
        foreach (var keyword in VirtualKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        return b[0] switch
        {
            10 => true,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            _ => false
        };
    }

    private static bool IsCarrierGradeNat(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static bool IsBenchmarkNetwork(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        return b[0] == 198 && (b[1] == 18 || b[1] == 19);
    }

    private static readonly string[] VirtualKeywords =
    [
        "Virtual",
        "Hyper-V",
        "VMware",
        "VirtualBox",
        "vEthernet",
        "WSL",
        "Tailscale",
        "ZeroTier",
        "WireGuard",
        "Wintun",
        "TAP",
        "TUN"
    ];
}

