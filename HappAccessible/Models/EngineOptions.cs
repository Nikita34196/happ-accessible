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
    /// <summary>Reject QUIC/UDP443 for xhttp/splithttp transports that stall on UDP.</summary>
    public bool RejectQuicUdp443 { get; init; } = true;

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
