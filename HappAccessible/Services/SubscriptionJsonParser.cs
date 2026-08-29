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
        {
            TryParseSingBoxOutbound(outbound, profiles);
            return;
        }
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

    private static void TryParseSingBoxOutbound(JsonElement outbound, List<ServerProfile> profiles)
    {
        if (!outbound.TryGetProperty("type", out var typeEl)
            || typeEl.GetString()?.Trim().ToLowerInvariant() is not
                ("vless" or "vmess" or "trojan" or "shadowsocks"))
            return;

        var protocol = typeEl.GetString()!.Trim().ToLowerInvariant();
        var host = GetString(outbound, "server");
        var port = GetInt(outbound, "server_port", 443);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return;

        var tag = GetString(outbound, "tag") ?? host;
        string uri;
        switch (protocol)
        {
            case "vless":
            {
                var uuid = GetString(outbound, "uuid");
                if (string.IsNullOrWhiteSpace(uuid))
                    return;
                var query = BuildSingBoxStreamQuery(outbound);
                var flow = GetString(outbound, "flow");
                uri = $"vless://{uuid}@{FormatHost(host)}:{port}?{query}";
                if (!string.IsNullOrWhiteSpace(flow))
                    uri += $"&flow={Uri.EscapeDataString(flow)}";
                break;
            }
            case "vmess":
            {
                var uuid = GetString(outbound, "uuid");
                if (string.IsNullOrWhiteSpace(uuid))
                    return;
                var json = JsonSerializer.Serialize(new
                {
                    v = "2", ps = tag, add = host, port, id = uuid, aid = 0,
                    net = GetString(outbound, "transport", "type") ?? "tcp",
                    type = "none", host = "", path = "",
                    tls = outbound.TryGetProperty("tls", out var tls)
                          && tls.ValueKind == JsonValueKind.Object
                          && GetBool(tls, "enabled") ? "tls" : ""
                });
                uri = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                break;
            }
            case "trojan":
            {
                var password = GetString(outbound, "password");
                if (string.IsNullOrWhiteSpace(password))
                    return;
                uri = $"trojan://{Uri.EscapeDataString(password)}@{FormatHost(host)}:{port}?" +
                      $"{BuildSingBoxStreamQuery(outbound)}";
                break;
            }
            default:
            {
                var method = GetString(outbound, "method");
                var password = GetString(outbound, "password");
                if (string.IsNullOrWhiteSpace(method) || password is null)
                    return;
                var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"));
                uri = $"ss://{cred}@{FormatHost(host)}:{port}";
                break;
            }
        }

        uri += $"#{Uri.EscapeDataString(tag)}";
        AddProfile(profiles, uri, tag, protocol == "shadowsocks" ? "ss" : protocol, host, port);
    }

    private static string BuildSingBoxStreamQuery(JsonElement outbound)
    {
        var parts = new List<string>();
        if (outbound.TryGetProperty("tls", out var tls)
            && tls.ValueKind == JsonValueKind.Object
            && GetBool(tls, "enabled"))
        {
            parts.Add("security=tls");
            AddQuery(parts, "sni", GetString(tls, "server_name"));
            if (tls.TryGetProperty("reality", out var reality)
                && reality.ValueKind == JsonValueKind.Object
                && GetBool(reality, "enabled"))
            {
                AddQuery(parts, "security", "reality");
                AddQuery(parts, "pbk", GetString(reality, "public_key"));
                AddQuery(parts, "sid", GetString(reality, "short_id"));
            }
        }

        if (outbound.TryGetProperty("transport", out var transport)
            && transport.ValueKind == JsonValueKind.Object)
        {
            var type = GetString(transport, "type");
            AddQuery(parts, "type", type);
            AddQuery(parts, "path", GetString(transport, "path"));
            AddQuery(parts, "serviceName", GetString(transport, "service_name"));
            if (transport.TryGetProperty("headers", out var headers)
                && headers.ValueKind == JsonValueKind.Object)
                AddQuery(parts, "host", GetString(headers, "Host") ?? GetString(headers, "host"));
        }

        return parts.Count == 0 ? "type=tcp" : string.Join("&", parts);
    }

    private static void AddQuery(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
    }

    private static string? GetString(JsonElement obj, string property, string? nested = null)
    {
        if (!obj.TryGetProperty(property, out var value))
            return null;
        if (nested is not null && value.ValueKind == JsonValueKind.Object)
            return GetString(value, nested);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool GetBool(JsonElement obj, string property)
    {
        return obj.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private static int GetInt(JsonElement obj, string property, int fallback)
    {
        return obj.TryGetProperty(property, out var value)
               && value.TryGetInt32(out var result) ? result : fallback;
    }

    private static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

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
                var uri = $"vless://{id}@{FormatHost(host!)}:{port}?{stream}";
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
            var uri = $"trojan://{password}@{FormatHost(host!)}:{port}?{stream}#{Uri.EscapeDataString(name ?? host ?? "trojan")}";
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
            var uri = $"ss://{cred}@{FormatHost(host!)}:{port}#{Uri.EscapeDataString(name ?? host ?? "ss")}";
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
        if (stream.TryGetProperty("wsSettings", out var ws)
            && ws.ValueKind == JsonValueKind.Object)
        {
            if (ws.TryGetProperty("path", out var path))
                parts.Add($"path={Uri.EscapeDataString(path.GetString() ?? "")}");
            if (ws.TryGetProperty("headers", out var headers)
                && headers.ValueKind == JsonValueKind.Object
                && headers.TryGetProperty("Host", out var host))
                parts.Add($"host={Uri.EscapeDataString(host.GetString() ?? "")}");
        }
        if (stream.TryGetProperty("grpcSettings", out var grpc)
            && grpc.ValueKind == JsonValueKind.Object
            && grpc.TryGetProperty("serviceName", out var service))
            parts.Add($"serviceName={Uri.EscapeDataString(service.GetString() ?? "")}");

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
