using System.Text;
using System.Text.Json;
using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>Builds Xray-core JSON from share links (proxy mode — HTTP inbound).</summary>
public static class XrayConfigBuilder
{
    public static string Build(ServerProfile server, int mixedPort)
    {
        var outbound = BuildOutbound(server)
                       ?? throw new InvalidOperationException(
                           $"Xray не поддерживает протокол «{server.Protocol}» (используйте sing-box).");

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["loglevel"] = "warning" },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "http-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = mixedPort,
                    ["protocol"] = "http",
                    ["settings"] = new Dictionary<string, object?>(),
                    ["sniffing"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new[] { "http", "tls", "quic" }
                    }
                },
                new Dictionary<string, object?>
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = mixedPort + 1,
                    ["protocol"] = "socks",
                    ["settings"] = new Dictionary<string, object?> { ["udp"] = true },
                    ["sniffing"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new[] { "http", "tls", "quic" }
                    }
                }
            },
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?>
                {
                    ["protocol"] = "freedom",
                    ["tag"] = "direct"
                },
                new Dictionary<string, object?>
                {
                    ["protocol"] = "blackhole",
                    ["tag"] = "block"
                }
            },
            ["routing"] = new Dictionary<string, object?>
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "field",
                        ["ip"] = new[] { "geoip:private" },
                        ["outboundTag"] = "direct"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object?>? BuildOutbound(ServerProfile server) =>
        server.Protocol.ToLowerInvariant() switch
        {
            "vless" => BuildVless(server.RawUri),
            "trojan" => BuildTrojan(server.RawUri),
            "vmess" => BuildVmess(server.RawUri),
            "ss" or "shadowsocks" => BuildShadowsocks(server.RawUri),
            _ => null
        };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0)
                dict[Uri.UnescapeDataString(part)] = "";
            else
                dict[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }

        return dict;
    }

    private static string? Q(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) ? v : null;

    private static Dictionary<string, object?> BuildVless(string uri)
    {
        var u = new Uri(uri);
        var q = ParseQuery(u.Query);
        var uuid = Uri.UnescapeDataString(u.UserInfo);
        var security = (Q(q, "security") ?? "none").ToLowerInvariant();
        var sni = Q(q, "sni") ?? Q(q, "host") ?? u.IdnHost;
        var flow = Q(q, "flow");
        var pbk = Q(q, "pbk");
        var sid = (Q(q, "sid") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var fp = Q(q, "fp") ?? "chrome";
        var type = (Q(q, "type") ?? "tcp").ToLowerInvariant();
        var path = Q(q, "path") ?? "/";
        var hostHeader = Q(q, "host");
        var spx = Q(q, "spx") ?? Q(q, "spiderX") ?? "";
        var port = u.Port > 0 ? u.Port : 443;

        var user = new Dictionary<string, object?> { ["id"] = uuid, ["encryption"] = "none" };
        if (!string.IsNullOrEmpty(flow) && type is "tcp" or "raw" or "")
            user["flow"] = flow;

        var stream = new Dictionary<string, object?> { ["network"] = type is "raw" ? "tcp" : type };

        if (security is "tls" or "reality")
        {
            stream["security"] = security;
            if (security == "reality")
            {
                stream["realitySettings"] = new Dictionary<string, object?>
                {
                    ["show"] = false,
                    ["fingerprint"] = fp,
                    ["serverName"] = sni,
                    ["publicKey"] = pbk ?? "",
                    ["shortId"] = sid,
                    ["spiderX"] = string.IsNullOrEmpty(spx) ? "" : spx
                };
            }
            else
            {
                stream["tlsSettings"] = new Dictionary<string, object?>
                {
                    ["serverName"] = sni,
                    ["fingerprint"] = fp,
                    ["allowInsecure"] = false
                };
            }
        }

        if (type is "ws")
        {
            stream["wsSettings"] = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["headers"] = string.IsNullOrEmpty(hostHeader)
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["Host"] = hostHeader }
            };
        }
        else if (type is "grpc")
        {
            stream["grpcSettings"] = new Dictionary<string, object?>
            {
                ["serviceName"] = Q(q, "serviceName") ?? Q(q, "path") ?? ""
            };
        }
        else if (type is "xhttp" or "splithttp")
        {
            // Newer Xray: xhttp
            stream["network"] = "xhttp";
            stream["xhttpSettings"] = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["host"] = hostHeader ?? sni,
                ["mode"] = Q(q, "mode") ?? "auto"
            };
        }

        return new Dictionary<string, object?>
        {
            ["protocol"] = "vless",
            ["tag"] = "proxy",
            ["settings"] = new Dictionary<string, object?>
            {
                ["vnext"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = u.IdnHost,
                        ["port"] = port,
                        ["users"] = new object[] { user }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static Dictionary<string, object?> BuildTrojan(string uri)
    {
        var u = new Uri(uri);
        var q = ParseQuery(u.Query);
        var password = Uri.UnescapeDataString(u.UserInfo);
        var sni = Q(q, "sni") ?? Q(q, "host") ?? u.IdnHost;
        var port = u.Port > 0 ? u.Port : 443;
        var type = (Q(q, "type") ?? "tcp").ToLowerInvariant();

        var stream = new Dictionary<string, object?>
        {
            ["network"] = type,
            ["security"] = "tls",
            ["tlsSettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = sni,
                ["allowInsecure"] = false
            }
        };
        if (type == "ws")
        {
            stream["wsSettings"] = new Dictionary<string, object?>
            {
                ["path"] = Q(q, "path") ?? "/",
                ["headers"] = new Dictionary<string, object?> { ["Host"] = sni }
            };
        }
        else if (type == "grpc")
        {
            stream["grpcSettings"] = new Dictionary<string, object?>
            {
                ["serviceName"] = Q(q, "serviceName") ?? Q(q, "service") ?? ""
            };
        }

        return new Dictionary<string, object?>
        {
            ["protocol"] = "trojan",
            ["tag"] = "proxy",
            ["settings"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = u.IdnHost,
                        ["port"] = port,
                        ["password"] = password
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static Dictionary<string, object?> BuildVmess(string uri)
    {
        // vmess://base64json
        var b64 = uri["vmess://".Length..];
        var pad = b64.Length % 4;
        if (pad > 0) b64 += new string('=', 4 - pad);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/')));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string S(string name) => root.TryGetProperty(name, out var e) ? e.ToString() : "";
        int Port() => root.TryGetProperty("port", out var e) && e.TryGetInt32(out var p) ? p :
            int.TryParse(S("port"), out var p2) ? p2 : 443;

        var network = (S("net") is { Length: > 0 } n ? n : "tcp").ToLowerInvariant();
        var tls = S("tls");
        var stream = new Dictionary<string, object?> { ["network"] = network };
        if (tls is "tls" or "reality")
        {
            stream["security"] = "tls";
            stream["tlsSettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = S("sni").Length > 0 ? S("sni") : S("host"),
                ["allowInsecure"] = false
            };
        }

        if (network == "ws")
        {
            stream["wsSettings"] = new Dictionary<string, object?>
            {
                ["path"] = S("path"),
                ["headers"] = new Dictionary<string, object?> { ["Host"] = S("host") }
            };
        }

        return new Dictionary<string, object?>
        {
            ["protocol"] = "vmess",
            ["tag"] = "proxy",
            ["settings"] = new Dictionary<string, object?>
            {
                ["vnext"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = S("add"),
                        ["port"] = Port(),
                        ["users"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = S("id"),
                                ["alterId"] = int.TryParse(S("aid"), out var aid) ? aid : 0,
                                ["security"] = S("scy").Length > 0 ? S("scy") : "auto"
                            }
                        }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static Dictionary<string, object?> BuildShadowsocks(string uri)
    {
        // ss://method:pass@host:port or ss://base64
        var rest = uri["ss://".Length..];
        string method, password, host;
        int port;
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];

        if (rest.Contains('@'))
        {
            var at = rest.LastIndexOf('@');
            var user = Uri.UnescapeDataString(rest[..at]);
            var hp = rest[(at + 1)..];
            var colon = user.IndexOf(':');
            method = colon > 0 ? user[..colon] : "aes-256-gcm";
            password = colon > 0 ? user[(colon + 1)..] : user;
            (host, port) = SplitHostPort(hp, 443);
        }
        else
        {
            var pad = rest.Length % 4;
            if (pad > 0) rest += new string('=', 4 - pad);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rest.Replace('-', '+').Replace('_', '/')));
            // method:password@host:port
            var at = decoded.LastIndexOf('@');
            var user = decoded[..at];
            var hp = decoded[(at + 1)..];
            var colon = user.IndexOf(':');
            method = user[..colon];
            password = user[(colon + 1)..];
            var lastColon = hp.LastIndexOf(':');
            host = hp[..lastColon];
            port = int.Parse(hp[(lastColon + 1)..]);
        }

        return new Dictionary<string, object?>
        {
            ["protocol"] = "shadowsocks",
            ["tag"] = "proxy",
            ["settings"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = host,
                        ["port"] = port,
                        ["method"] = method,
                        ["password"] = password
                    }
                }
            }
        };
    }

    private static (string Host, int Port) SplitHostPort(string value, int fallbackPort)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end <= 1)
                throw new FormatException("Некорректный IPv6 host.");
            var port = end + 1 < value.Length && value[end + 1] == ':'
                && int.TryParse(value[(end + 2)..], out var parsed)
                ? parsed
                : fallbackPort;
            return (value[1..end], port);
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out var p))
            return (value[..colon], p);
        return (value, fallbackPort);
    }
}
