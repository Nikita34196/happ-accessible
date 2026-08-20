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

        // TUN / app routing / geosite modes need sing-box (lx fork supports xhttp)
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

        var uri = server.RawUri ?? "";

        // Auto without TUN: Reality / Vision → Xray (stable); xhttp also fine on Xray
        if (LooksRealityOrVision(uri) || NeedsXrayTransport(uri))
            return ProxyCoreKind.Xray;

        return ProxyCoreKind.SingBox;
    }

    /// <summary>XHTTP / SplitHTTP — stock sing-box lacks these; lx fork and Xray support them.</summary>
    public static bool NeedsXrayTransport(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;
        return ContainsQueryToken(uri, "type", "xhttp")
               || ContainsQueryToken(uri, "type", "splithttp")
               || ContainsQueryToken(uri, "network", "xhttp")
               || ContainsQueryToken(uri, "network", "splithttp")
               || ContainsQueryToken(uri, "net", "xhttp")
               || ContainsQueryToken(uri, "net", "splithttp")
               || uri.Contains("\"network\":\"xhttp\"", StringComparison.OrdinalIgnoreCase)
               || uri.Contains("\"network\": \"xhttp\"", StringComparison.OrdinalIgnoreCase)
               || uri.Contains("network: xhttp", StringComparison.OrdinalIgnoreCase)
               || uri.Contains("network:xhttp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsQueryToken(string uri, string key, string value)
    {
        var needle = key + "=" + value;
        var i = uri.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return false;
        var end = i + needle.Length;
        if (end < uri.Length)
        {
            var c = uri[end];
            if (c is not ('&' or '#' or '/' or '?') && !char.IsWhiteSpace(c))
                return false;
        }

        return true;
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
