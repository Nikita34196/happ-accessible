using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HappAccessible.Models;

namespace HappAccessible.Services;

public static class HappRoutingProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible",
            "routing-profiles.json");

    public static IReadOnlyList<HappRoutingProfile> LoadAll()
    {
        try
        {
            if (!File.Exists(StorePath))
                return [];
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<List<HappRoutingProfile>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveAll(IEnumerable<HappRoutingProfile> profiles)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var list = profiles.ToList();
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(tmp, StorePath, overwrite: true);
    }

    public static HappRoutingProfile? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return LoadAll().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    public static (HappRoutingProfile Profile, bool Added) Import(HappRoutingProfile profile)
    {
        profile.Name = profile.Name.Trim();
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("В профиле нет имени (Name).");

        var all = LoadAll().ToList();
        var existing = all.FirstOrDefault(p =>
            string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            profile.Id = existing.Id;
            all.Remove(existing);
            all.Add(profile);
            SaveAll(all);
            return (profile, false);
        }

        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Guid.NewGuid().ToString("N");
        all.Add(profile);
        SaveAll(all);
        return (profile, true);
    }

    public static bool Remove(string id)
    {
        var all = LoadAll().ToList();
        var removed = all.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
        if (removed)
            SaveAll(all);
        return removed;
    }
}

public static class HappRoutingImporter
{
    public static bool LooksLikeHappRouting(string text) => LooksLikeRoutingLink(text);

    public static bool LooksLikeRoutingLink(string text)
    {
        text = text.Trim();
        return text.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("incy://routing/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("incy://autorouting/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("incy://onadd/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("://routing/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("://autorouting/", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("://onadd/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeRoutingJson(string text)
    {
        if (!TryDecodePayload(text.Trim(), out var json))
            return false;
        return JsonLooksLikeRoutingProfile(json);
    }

    public static HappRoutingProfile Parse(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Пустая ссылка профиля маршрутизации.");

        text = StripRoutingPrefix(text);

        if (!TryDecodePayload(text, out var json))
            throw new InvalidOperationException("Не удалось разобрать профиль маршрутизации Happ/INCY.");

        HappRoutingProfile profile;
        try
        {
            profile = ParseJson(json);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Профиль маршрутизации повреждён: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("В профиле нет имени (Name).");

        profile.RemoteDnsIp = NormalizeHost(profile.RemoteDnsIp, "1.1.1.1");
        profile.DomesticDnsIp = NormalizeHost(profile.DomesticDnsIp, "1.0.0.1");
        profile.RemoteDnsType = NormalizeDnsType(profile.RemoteDnsType, "DoH");
        profile.DomesticDnsType = NormalizeDnsType(profile.DomesticDnsType, "DoU");
        return profile;
    }

    private static string StripRoutingPrefix(string text)
    {
        string[] prefixes =
        [
            "happ://routing/add/",
            "happ://routing/onadd/",
            "incy://routing/add/",
            "incy://routing/onadd/",
            "incy://autorouting/add/",
            "incy://autorouting/onadd/",
            "incy://onadd/",
            "://routing/add/",
            "://routing/onadd/",
            "://autorouting/add/",
            "://autorouting/onadd/",
            "://onadd/"
        ];
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..];
        }

        return text;
    }

    private static bool JsonLooksLikeRoutingProfile(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var root = doc.RootElement;
            if (!HasProperty(root, "Name"))
                return false;
            return HasProperty(root, "RemoteDNSType")
                   || HasProperty(root, "RemoteDNSIp")
                   || HasProperty(root, "RemoteDNSIP")
                   || HasProperty(root, "DirectSites")
                   || HasProperty(root, "GlobalProxy")
                   || HasProperty(root, "FakeDNS")
                   || HasProperty(root, "FakeDns")
                   || HasProperty(root, "DnsHosts");
        }
        catch
        {
            return false;
        }
    }

    private static HappRoutingProfile ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var profile = new HappRoutingProfile
        {
            Name = ReadString(root, "Name") ?? "",
            GlobalProxy = ReadBool(root, true, "GlobalProxy"),
            RemoteDnsIp = ReadString(root, "RemoteDNSIP", "RemoteDNSIp", "RemoteDns") ?? "1.1.1.1",
            RemoteDnsDomain = ReadString(root, "RemoteDNSDomain") ?? "",
            RemoteDnsType = ReadString(root, "RemoteDNSType") ?? "DoH",
            DomesticDnsIp = ReadString(root, "DomesticDNSIP", "DomesticDNSIp", "DomesticDns") ?? "1.0.0.1",
            DomesticDnsDomain = ReadString(root, "DomesticDNSDomain") ?? "",
            DomesticDnsType = ReadString(root, "DomesticDNSType") ?? "DoU",
            FakeDns = ReadBool(root, false, "FakeDNS", "FakeDns"),
            DomainStrategy = ReadString(root, "DomainStrategy") ?? "IPIfNonMatch",
            DirectSites = ReadStringList(root, "DirectSites"),
            DirectIp = ReadStringList(root, "DirectIp"),
            ProxySites = ReadStringList(root, "ProxySites"),
            ProxyIp = ReadStringList(root, "ProxyIp"),
            BlockSites = ReadStringList(root, "BlockSites"),
            BlockIp = ReadStringList(root, "BlockIp")
        };

        if (TryGetProperty(root, out var hosts, "DnsHosts") && hosts.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hosts.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    profile.DnsHosts[prop.Name] = value!;
            }
        }

        return profile;
    }

    private static bool HasProperty(JsonElement root, string name) =>
        TryGetProperty(root, out _, name);

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        if (!TryGetProperty(root, out var el, names))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool ReadBool(JsonElement root, bool fallback, params string[] names)
    {
        if (!TryGetProperty(root, out var el, names))
            return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => ParseBool(el.GetString(), fallback),
            _ => fallback
        };
    }

    private static bool ParseBool(string? text, bool fallback)
    {
        text = (text ?? "").Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("1", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text.Equals("0", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;
        return fallback;
    }

    private static List<string> ReadStringList(JsonElement root, string name)
    {
        if (!TryGetProperty(root, out var el, name) || el.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s!);
        }

        return list;
    }

    private static bool TryDecodePayload(string text, out string json)
    {
        json = "";
        text = text.Trim().Trim('"');
        if (text.StartsWith('{'))
        {
            json = text;
            return true;
        }

        try
        {
            var b64 = text;
            var pad = b64.Length % 4;
            if (pad > 0)
                b64 = b64.PadRight(b64.Length + (4 - pad), '=');
            json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return json.TrimStart().StartsWith('{');
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeHost(string? host, string fallback)
    {
        host = (host ?? "").Trim();
        return host.Length > 0 ? host : fallback;
    }

    private static string NormalizeDnsType(string? type, string fallback)
    {
        type = (type ?? "").Trim();
        if (type.Length == 0)
            return fallback;
        return type.ToUpperInvariant() switch
        {
            "DOH" => "DoH",
            "DOU" => "DoU",
            "DOT" => "DoT",
            "LOCAL" => "Local",
            _ => type
        };
    }
}
