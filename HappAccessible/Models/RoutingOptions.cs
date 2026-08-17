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
