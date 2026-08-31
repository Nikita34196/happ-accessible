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
    public static bool LooksLikeHappRouting(string text)
    {
        text = text.Trim();
        return text.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase)
               || TryDecodePayload(text, out _);
    }

    public static HappRoutingProfile Parse(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Пустая ссылка профиля маршрутизации.");

        if (text.StartsWith("happ://routing/add/", StringComparison.OrdinalIgnoreCase))
            text = text["happ://routing/add/".Length..];

        if (!TryDecodePayload(text, out var json))
            throw new InvalidOperationException("Не удалось разобрать профиль маршрутизации Happ.");

        var profile = JsonSerializer.Deserialize<HappRoutingProfile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Профиль маршрутизации пуст.");

        profile.RemoteDnsIp = NormalizeHost(profile.RemoteDnsIp, "1.1.1.1");
        profile.DomesticDnsIp = NormalizeHost(profile.DomesticDnsIp, "1.0.0.1");
        profile.RemoteDnsType = NormalizeDnsType(profile.RemoteDnsType, "DoH");
        profile.DomesticDnsType = NormalizeDnsType(profile.DomesticDnsType, "DoU");
        return profile;
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
