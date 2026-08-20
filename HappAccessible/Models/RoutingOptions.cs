using System.IO;

namespace HappAccessible.Models;

public enum RoutingMode
{
    /// <summary>All traffic via VPN (LAN stays direct).</summary>
    Global = 0,
    /// <summary>Only listed domains/suffixes via VPN; everything else direct.</summary>
    ProxyList = 1,
    /// <summary>All via VPN except listed domains/suffixes.</summary>
    BypassList = 2,
    /// <summary>All via VPN except Russian sites/IPs (gosuslugi, .ru geo, etc.).</summary>
    BypassRu = 3,
    /// <summary>Only listed Windows processes via VPN (needs TUN).</summary>
    AppProxy = 4,
    /// <summary>All via VPN except listed Windows processes (needs TUN).</summary>
    AppBypass = 5
}

public sealed class RoutingOptions
{
    public RoutingMode Mode { get; init; } = RoutingMode.Global;
    public IReadOnlyList<string> Domains { get; init; } = [];
    public IReadOnlyList<string> Processes { get; init; } = [];

    public static IReadOnlyList<string> ParseDomainList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var list = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Keep geosite:/geoip:/rule-set: tags as-is for SingBoxConfigBuilder
            if (line.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("rule-set:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("ruleset:", StringComparison.OrdinalIgnoreCase))
            {
                if (!list.Contains(line, StringComparer.OrdinalIgnoreCase))
                    list.Add(line);
                continue;
            }

            line = line.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("http://", "", StringComparison.OrdinalIgnoreCase);
            var slash = line.IndexOf('/');
            if (slash >= 0)
                line = line[..slash];
            line = line.Trim().TrimStart('.').ToLowerInvariant();
            if (line.Length > 0 && !list.Contains(line, StringComparer.OrdinalIgnoreCase))
                list.Add(line);
        }

        return list;
    }

    public static (IReadOnlyList<string> Domains, IReadOnlyList<string> GeoSite, IReadOnlyList<string> GeoIp)
        SplitRoutingTags(IReadOnlyList<string> entries)
    {
        var domains = new List<string>();
        var geosite = new List<string>();
        var geoip = new List<string>();
        foreach (var e in entries)
        {
            if (e.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = e["geosite:".Length..].Trim();
                if (tag.Length > 0)
                    geosite.Add(tag);
            }
            else if (e.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = e["geoip:".Length..].Trim();
                if (tag.Length > 0)
                    geoip.Add(tag);
            }
            else if (e.StartsWith("rule-set:", StringComparison.OrdinalIgnoreCase)
                     || e.StartsWith("ruleset:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = e[(e.IndexOf(':') + 1)..].Trim();
                // Prefer treating as geosite tag unless it looks like geoip-
                if (rest.StartsWith("geoip-", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("geoip_", StringComparison.OrdinalIgnoreCase))
                    geoip.Add(rest.Contains(':') ? rest[(rest.IndexOf(':') + 1)..] : rest.Replace("geoip-", "", StringComparison.OrdinalIgnoreCase));
                else if (rest.StartsWith("geosite-", StringComparison.OrdinalIgnoreCase))
                    geosite.Add(rest["geosite-".Length..]);
                else
                    geosite.Add(rest);
            }
            else
            {
                domains.Add(e);
            }
        }

        return (domains, geosite, geoip);
    }

    /// <summary>Process names for sing-box process_name (e.g. chrome.exe).</summary>
    public static IReadOnlyList<string> ParseProcessList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var list = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Full path → file name
            if (line.Contains('\\') || line.Contains('/'))
                line = Path.GetFileName(line);

            if (!line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !line.Contains('.'))
                line += ".exe";

            line = line.ToLowerInvariant();
            if (line.Length > 0 && !list.Contains(line, StringComparer.OrdinalIgnoreCase))
                list.Add(line);
        }

        return list;
    }
}
