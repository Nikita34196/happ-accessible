namespace HappAccessible.Models;

public sealed class ServerProfile
{
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public required string RawUri { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public int? LatencyMs { get; set; }

    /// <summary>Set by UI when classifying whitelist-bypass nodes.</summary>
    public bool IsWhitelistBypass { get; set; }

    public string DisplayName
    {
        get
        {
            var ping = LatencyMs is null ? "нет ответа"
                : LatencyMs < 0 ? "…"
                : $"{LatencyMs} мс";
            var mark = IsWhitelistBypass ? "обход БС, " : "";
            return $"{Name} ({mark}{Protocol}, {ping})";
        }
    }

    public override string ToString() => DisplayName;
}
