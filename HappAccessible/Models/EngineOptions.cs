namespace HappAccessible.Models;

/// <summary>sing-box runtime options (port, TUN stack).</summary>
public sealed class EngineOptions
{
    public const int DefaultMixedPort = 2080;
    public const string DefaultTunStack = "gvisor";

    public int MixedPort { get; init; } = DefaultMixedPort;
    /// <summary>gvisor | mixed | system</summary>
    public string TunStack { get; init; } = DefaultTunStack;
    /// <summary>ipv4_only | prefer_ipv4 | prefer_ipv6 | ipv6_only</summary>
    public string DnsStrategy { get; init; } = "ipv4_only";
    public string DnsRemoteServer { get; init; } = "1.1.1.1";
    public string DnsRemoteFallback { get; init; } = "8.8.8.8";
    /// <summary>DoH | DoU | DoT</summary>
    public string DnsRemoteType { get; init; } = "DoH";
    public string DnsRemoteDomain { get; init; } = "";
    public string DnsDomesticServer { get; init; } = "1.0.0.1";
    /// <summary>DoH | DoU | DoT</summary>
    public string DnsDomesticType { get; init; } = "DoU";
    public string DnsDomesticDomain { get; init; } = "";
    public bool FakeDns { get; init; }
    public IReadOnlyDictionary<string, string> DnsHosts { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public HappRoutingProfile? RoutingProfile { get; init; }
    /// <summary>Reject QUIC/UDP443 for xhttp/splithttp transports that stall on UDP.</summary>
    public bool RejectQuicUdp443 { get; init; } = true;

    public static EngineOptions FromProfile(HappRoutingProfile profile, EngineOptions defaults)
    {
        var strategy = profile.DomainStrategy.Trim().ToUpperInvariant() switch
        {
            "IPIFNONMATCH" => "prefer_ipv4",
            "PREFERIPV6" => "prefer_ipv6",
            "PREFERIPV4" => "prefer_ipv4",
            "IPV6ONLY" => "ipv6_only",
            _ => defaults.DnsStrategy
        };

        return new EngineOptions
        {
            MixedPort = defaults.MixedPort,
            TunStack = defaults.TunStack,
            DnsStrategy = strategy,
            DnsRemoteServer = string.IsNullOrWhiteSpace(profile.RemoteDnsIp) ? defaults.DnsRemoteServer : profile.RemoteDnsIp.Trim(),
            DnsRemoteFallback = defaults.DnsRemoteFallback,
            DnsRemoteType = string.IsNullOrWhiteSpace(profile.RemoteDnsType) ? defaults.DnsRemoteType : profile.RemoteDnsType.Trim(),
            DnsRemoteDomain = profile.RemoteDnsDomain?.Trim() ?? "",
            DnsDomesticServer = string.IsNullOrWhiteSpace(profile.DomesticDnsIp) ? defaults.DnsDomesticServer : profile.DomesticDnsIp.Trim(),
            DnsDomesticType = string.IsNullOrWhiteSpace(profile.DomesticDnsType) ? defaults.DnsDomesticType : profile.DomesticDnsType.Trim(),
            DnsDomesticDomain = profile.DomesticDnsDomain?.Trim() ?? "",
            FakeDns = profile.FakeDns,
            DnsHosts = profile.DnsHosts.Count > 0
                ? new Dictionary<string, string>(profile.DnsHosts, StringComparer.OrdinalIgnoreCase)
                : defaults.DnsHosts,
            RoutingProfile = profile,
            RejectQuicUdp443 = defaults.RejectQuicUdp443
        };
    }

    public static int ClampPort(int port) =>
        port is >= 1024 and <= 65535 ? port : DefaultMixedPort;

    public static string NormalizeTunStack(string? stack)
    {
        stack = (stack ?? "").Trim().ToLowerInvariant();
        return stack switch
        {
            "mixed" => "mixed",
            "system" => "system",
            _ => "gvisor"
        };
    }
}
