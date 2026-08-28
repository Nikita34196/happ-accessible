namespace HappAccessible.Models;

public sealed class ServerProfile
{
    public required string Name { get; set; }
    public required string Protocol { get; init; }
    public required string RawUri { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public int? LatencyMs { get; set; }

    /// <summary>Original name from subscription before local rename.</summary>
    public string? OriginalName { get; set; }

    /// <summary>Set by UI when classifying whitelist-bypass nodes.</summary>
    public bool IsWhitelistBypass { get; set; }

    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }

    public string DisplayName
    {
        get
        {
            var ping = LatencyMs is null ? "нет ответа TCP"
                : LatencyMs < 0 ? "…"
                : $"TCP {LatencyMs} мс";
            var fav = IsFavorite ? "★, " : "";
            var mark = IsWhitelistBypass ? "обход БС, " : "";
            var when = LastSuccessUtc is { } ok
                ? $", успех {ok.LocalDateTime:dd.MM HH:mm}"
                : "";
            return $"{Name} ({fav}{mark}{Protocol}, {ping}{when})";
        }
    }

    public override string ToString() => DisplayName;
}
