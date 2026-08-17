using System.Text.RegularExpressions;
using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>
/// Heuristics for Russian ISP "whitelist" bypass nodes (RU exits, bridges, labelled whitelist).
/// </summary>
public static class ServerClassifier
{
    private static readonly Regex WhitelistPattern = new(
        @"белый\s*список|белы[йе]\s*спис|whitelist|\bwl\b|обход|bridge|\bbrdg\b|" +
        @"для\s*рф|для\s*россии|whitelist.?bypass|white.?list|" +
        @"россия|москва|новосибирск|екатеринбург|казань|санкт.?петербург|" +
        @"спб\b|нск\b|екб\b|кзн\b|" +
        @"premium-ru|geodema\.network.*\bru\b|\.ru\.|/ru-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsWhitelistBypass(ServerProfile server)
    {
        var blob = $"{server.Name} {server.Host} {server.RawUri}";
        return WhitelistPattern.IsMatch(blob);
    }

    public static IEnumerable<ServerProfile> PreferWhitelistBypass(IEnumerable<ServerProfile> servers) =>
        servers
            .OrderByDescending(IsWhitelistBypass)
            .ThenBy(s => s.LatencyMs is > 0 ? s.LatencyMs.Value : int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ServerProfile> WhitelistOnly(IReadOnlyList<ServerProfile> servers) =>
        servers.Where(IsWhitelistBypass).ToList();
}
