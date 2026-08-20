using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>Chooses sing-box vs Xray for a connection.</summary>
public static class CoreSelector
{
    public static ProxyCoreKind Resolve(
        ServerProfile server,
        ProxyCoreKind preference,
        bool useTun,
        RoutingMode routingMode)
    {
        if (string.Equals(server.Protocol, "amneziawg", StringComparison.OrdinalIgnoreCase))
            return ProxyCoreKind.SingBox; // unused — AWG path is separate

        // TUN / app routing / geosite modes need sing-box
        if (useTun
            || routingMode is RoutingMode.AppProxy or RoutingMode.AppBypass
                or RoutingMode.BypassRu or RoutingMode.ProxyList or RoutingMode.BypassList)
        {
            return ProxyCoreKind.SingBox;
        }

        if (string.Equals(server.Protocol, "hysteria2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.Protocol, "hy2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.Protocol, "hysteria", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.Protocol, "wg", StringComparison.OrdinalIgnoreCase))
            return ProxyCoreKind.SingBox;

        if (preference == ProxyCoreKind.SingBox)
            return ProxyCoreKind.SingBox;
        if (preference == ProxyCoreKind.Xray)
            return ProxyCoreKind.Xray;

        // Auto: Reality / Vision → Xray (proxy mode)
        var uri = server.RawUri ?? "";
        if (LooksRealityOrVision(uri))
            return ProxyCoreKind.Xray;

        return ProxyCoreKind.SingBox;
    }

    public static bool LooksRealityOrVision(string uri) =>
        uri.Contains("security=reality", StringComparison.OrdinalIgnoreCase)
        || uri.Contains("pbk=", StringComparison.OrdinalIgnoreCase)
        || uri.Contains("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase)
        || uri.Contains("flow=xtls", StringComparison.OrdinalIgnoreCase);

    public static string DisplayName(ProxyCoreKind kind) => kind switch
    {
        ProxyCoreKind.Xray => "Xray",
        ProxyCoreKind.SingBox => "sing-box",
        _ => "auto"
    };
}
