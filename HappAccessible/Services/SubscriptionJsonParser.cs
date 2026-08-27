using System.Text;
using System.Text.Json;
using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>Extract server URIs from Xray / sing-box JSON subscription bodies.</summary>
public static class SubscriptionJsonParser
{
    public static IReadOnlyList<ServerProfile> TryParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(content);
            var profiles = new List<ServerProfile>();
            CollectFromElement(doc.RootElement, profiles);
            return profiles;
        }
        catch
        {
            return [];
        }
    }

    private static void CollectFromElement(JsonElement root, List<ServerProfile> profiles)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                CollectFromElement(item, profiles);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            foreach (var outbound in outbounds.EnumerateArray())
                TryParseOutbound(outbound, profiles);
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectFromElement(prop.Value, profiles);
        }
    }

    private static void TryParseOutbound(JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!outbound.TryGetProperty("protocol", out var protocolEl))
            return;
        var protocol = protocolEl.GetString()?.Trim().ToLowerInvariant();
        if (protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "ss"))
            return;

        if (!outbound.TryGetProperty("settings", out var settings))
            return;

        switch (protocol)
        {
            case "vless":
                ParseVless(settings, outbound, profiles);
                break;
            case "vmess":
                ParseVmess(settings, outbound, profiles);
                break;
            case "trojan":
                ParseTrojan(settings, outbound, profiles);
                break;
            case "shadowsocks":
            case "ss":
                ParseShadowsocks(settings, outbound, profiles);
                break;
        }
    }

    private static void ParseVless(JsonElement settings, JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!settings.TryGetProperty("vnext", out var vnext) || vnext.ValueKind != JsonValueKind.Array)
            return;

        foreach (var node in vnext.EnumerateArray())
        {
            var host = node.GetProperty("address").GetString();
            var port = node.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 443;
            if (string.IsNullOrWhiteSpace(host))
                continue;

            foreach (var user in node.GetProperty("users").EnumerateArray())
            {
                var id = user.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var flow = user.TryGetProperty("flow", out var flowEl) ? flowEl.GetString() : null;
                var stream = BuildStreamQuery(outbound);
                var name = outbound.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : host;
                var uri = $"vless://{id}@{host}:{port}?{stream}";
                if (!string.IsNullOrWhiteSpace(flow))
                    uri += $"&flow={Uri.EscapeDataString(flow)}";
                uri += $"#{Uri.EscapeDataString(name ?? host)}";
                AddProfile(profiles, uri, name ?? host, "vless", host, port);
            }
        }
    }

    private static void ParseVmess(JsonElement settings, JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!settings.TryGetProperty("vnext", out var vnext) || vnext.ValueKind != JsonValueKind.Array)
            return;

        foreach (var node in vnext.EnumerateArray())
        {
            var host = node.GetProperty("address").GetString();
            var port = node.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 443;
            foreach (var user in node.GetProperty("users").EnumerateArray())
            {
                var id = user.GetProperty("id").GetString();
                var name = outbound.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : host;
                var json = JsonSerializer.Serialize(new
                {
                    v = "2",
                    ps = name,
                    add = host,
                    port,
                    id,
                    aid = user.TryGetProperty("alterId", out var aidEl) ? aidEl.GetInt32() : 0,
                    net = outbound.TryGetProperty("streamSettings", out var ss)
                          && ss.TryGetProperty("network", out var netEl)
                        ? netEl.GetString()
                        : "tcp",
                    type = "none",
                    host = "",
                    path = "",
                    tls = ss.ValueKind == JsonValueKind.Object
                          && ss.TryGetProperty("security", out var secEl)
                          && string.Equals(secEl.GetString(), "tls", StringComparison.OrdinalIgnoreCase)
                        ? "tls"
                        : ""
                });
                var uri = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                AddProfile(profiles, uri, name ?? host ?? "vmess", "vmess", host, port);
            }
        }
    }

    private static void ParseTrojan(JsonElement settings, JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!settings.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return;

        foreach (var server in servers.EnumerateArray())
        {
            var host = server.GetProperty("address").GetString();
            var port = server.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 443;
            var password = server.GetProperty("password").GetString();
            var name = outbound.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : host;
            var stream = BuildStreamQuery(outbound);
            var uri = $"trojan://{password}@{host}:{port}?{stream}#{Uri.EscapeDataString(name ?? host ?? "trojan")}";
            AddProfile(profiles, uri, name ?? host ?? "trojan", "trojan", host, port);
        }
    }

    private static void ParseShadowsocks(JsonElement settings, JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!settings.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return;

        foreach (var server in servers.EnumerateArray())
        {
            var host = server.GetProperty("address").GetString();
            var port = server.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 8388;
            var method = server.GetProperty("method").GetString();
            var password = server.GetProperty("password").GetString();
            var name = outbound.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : host;
            var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"));
            var uri = $"ss://{cred}@{host}:{port}#{Uri.EscapeDataString(name ?? host ?? "ss")}";
            AddProfile(profiles, uri, name ?? host ?? "ss", "ss", host, port);
        }
    }

    private static string BuildStreamQuery(JsonElement outbound)
    {
        if (!outbound.TryGetProperty("streamSettings", out var stream) || stream.ValueKind != JsonValueKind.Object)
            return "type=tcp";

        var parts = new List<string>();
        if (stream.TryGetProperty("network", out var netEl) && netEl.GetString() is { } net)
            parts.Add($"type={Uri.EscapeDataString(net)}");
        if (stream.TryGetProperty("security", out var secEl) && secEl.GetString() is { } sec && sec != "none")
            parts.Add($"security={Uri.EscapeDataString(sec)}");
        if (stream.TryGetProperty("realitySettings", out var reality))
        {
            if (reality.TryGetProperty("publicKey", out var pk))
                parts.Add($"pbk={Uri.EscapeDataString(pk.GetString() ?? "")}");
            if (reality.TryGetProperty("shortId", out var sid))
                parts.Add($"sid={Uri.EscapeDataString(sid.GetString() ?? "")}");
            if (reality.TryGetProperty("serverName", out var sn))
                parts.Add($"sni={Uri.EscapeDataString(sn.GetString() ?? "")}");
        }

        return parts.Count > 0 ? string.Join("&", parts) : "type=tcp";
    }

    private static void AddProfile(
        List<ServerProfile> profiles,
        string rawUri,
        string name,
        string protocol,
        string? host,
        int port)
    {
        var parsed = SubscriptionParser.ParseLinePublic(rawUri);
        if (parsed is not null && !SubscriptionParser.IsDummyStub(parsed))
        {
            profiles.Add(parsed);
            return;
        }

        profiles.Add(new ServerProfile
        {
            Name = name,
            Protocol = protocol,
            RawUri = rawUri,
            Host = host,
            Port = port
        });
    }
}
