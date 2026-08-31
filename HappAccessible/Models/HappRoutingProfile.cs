using System.Text.Json.Serialization;

namespace HappAccessible.Models;

/// <summary>Happ-compatible routing/DNS profile (happ://routing/add/…).</summary>
public sealed class HappRoutingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("GlobalProxy")]
    public bool GlobalProxy { get; set; } = true;

    [JsonPropertyName("RemoteDNSIp")]
    public string RemoteDnsIp { get; set; } = "1.1.1.1";

    [JsonPropertyName("RemoteDNSDomain")]
    public string RemoteDnsDomain { get; set; } = "";

    [JsonPropertyName("RemoteDNSType")]
    public string RemoteDnsType { get; set; } = "DoH";

    [JsonPropertyName("DomesticDNSIp")]
    public string DomesticDnsIp { get; set; } = "1.0.0.1";

    [JsonPropertyName("DomesticDNSDomain")]
    public string DomesticDnsDomain { get; set; } = "";

    [JsonPropertyName("DomesticDNSType")]
    public string DomesticDnsType { get; set; } = "DoU";

    [JsonPropertyName("FakeDns")]
    public bool FakeDns { get; set; }

    [JsonPropertyName("DnsHosts")]
    public Dictionary<string, string> DnsHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("DirectSites")]
    public List<string> DirectSites { get; set; } = [];

    [JsonPropertyName("DirectIp")]
    public List<string> DirectIp { get; set; } = [];

    [JsonPropertyName("ProxySites")]
    public List<string> ProxySites { get; set; } = [];

    [JsonPropertyName("ProxyIp")]
    public List<string> ProxyIp { get; set; } = [];

    [JsonPropertyName("BlockSites")]
    public List<string> BlockSites { get; set; } = [];

    [JsonPropertyName("BlockIp")]
    public List<string> BlockIp { get; set; } = [];

    [JsonPropertyName("DomainStrategy")]
    public string DomainStrategy { get; set; } = "IPIfNonMatch";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "Профиль маршрутизации" : Name.Trim();
}
