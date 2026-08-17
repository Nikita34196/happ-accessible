namespace HappAccessible.Models;

/// <summary>sing-box runtime options (port, TUN stack).</summary>
public sealed class EngineOptions
{
    public const int DefaultMixedPort = 2080;
    public const string DefaultTunStack = "gvisor";

    public int MixedPort { get; init; } = DefaultMixedPort;
    /// <summary>gvisor | mixed | system</summary>
    public string TunStack { get; init; } = DefaultTunStack;

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
