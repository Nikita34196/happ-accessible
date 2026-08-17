using System.IO;
using System.Text;
using System.Text.Json;
using HappAccessible.Models;

namespace HappAccessible.Services;

public static class SingBoxConfigBuilder
{
    public const int DefaultMixedPort = EngineOptions.DefaultMixedPort;
    public const int MixedPort = DefaultMixedPort;

    public static string Build(ServerProfile server, bool enableTun, RoutingOptions? routing = null,
        EngineOptions? engine = null)
    {
        routing ??= new RoutingOptions();
        engine ??= new EngineOptions();
        var mixedPort = EngineOptions.ClampPort(engine.MixedPort);
        var tunStack = EngineOptions.NormalizeTunStack(engine.TunStack);

        var outbound = BuildOutbound(server)
                       ?? throw new InvalidOperationException(
                           $"Не удалось собрать outbound для протокола «{server.Protocol}».");

        var inbounds = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = mixedPort
            }
        };

        if (enableTun)
        {
            inbounds.Add(new Dictionary<string, object?>
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "ha-tun",
                ["address"] = new[] { "172.19.0.1/30" },
                ["mtu"] = 1500,
                ["auto_route"] = true,
                ["strict_route"] = false,
                ["stack"] = tunStack
            });
        }

        var rules = new List<object>
        {
            new Dictionary<string, object?> { ["action"] = "sniff" },
            new Dictionary<string, object?>
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            },
            new Dictionary<string, object?>
            {
                ["ip_is_private"] = true,
                ["outbound"] = "direct"
            }
        };

        // Never send traffic to the VPN node itself through the tunnel (TUN loop)
        if (!string.IsNullOrWhiteSpace(server.Host))
        {
            if (System.Net.IPAddress.TryParse(server.Host, out var ip))
            {
                var cidr = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"{server.Host}/128"
                    : $"{server.Host}/32";
                rules.Add(new Dictionary<string, object?>
                {
                    ["ip_cidr"] = new[] { cidr },
                    ["outbound"] = "direct"
                });
            }
            else
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["domain"] = new[] { server.Host },
                    ["outbound"] = "direct"
                });
            }
        }

        var finalOutbound = "proxy";
        var dnsFinal = "dns-remote";
        List<object>? ruleSets = null;
        List<object>? dnsRules = null;

        if (routing.Mode == RoutingMode.ProxyList)
        {
            finalOutbound = "direct";
            dnsFinal = "dns-local";
            if (routing.Domains.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["domain_suffix"] = routing.Domains.ToArray(),
                    ["outbound"] = "proxy"
                });
                dnsRules =
                [
                    new Dictionary<string, object?>
                    {
                        ["domain_suffix"] = routing.Domains.ToArray(),
                        ["server"] = "dns-remote"
                    }
                ];
            }
        }
        else if (routing.Mode == RoutingMode.BypassList)
        {
            finalOutbound = "proxy";
            dnsFinal = "dns-remote";
            if (routing.Domains.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["domain_suffix"] = routing.Domains.ToArray(),
                    ["outbound"] = "direct"
                });
            }
        }
        else if (routing.Mode == RoutingMode.BypassRu)
        {
            // Foreign via VPN; Russian sites/IPs and common RU gov domains — direct
            finalOutbound = "proxy";
            dnsFinal = "dns-remote";

            var ruDomains = GetBuiltInRussianDomains();
            if (routing.Domains.Count > 0)
                ruDomains = ruDomains.Concat(routing.Domains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            rules.Add(new Dictionary<string, object?>
            {
                ["domain_suffix"] = ruDomains,
                ["outbound"] = "direct"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = new[] { "geosite-category-ru", "geosite-category-gov-ru" },
                ["outbound"] = "direct"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = "geoip-ru",
                ["outbound"] = "direct"
            });

            ruleSets =
            [
                RemoteRuleSet("geoip-ru",
                    "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs"),
                RemoteRuleSet("geosite-category-ru",
                    "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-category-ru.srs"),
                RemoteRuleSet("geosite-category-gov-ru",
                    "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-category-gov-ru.srs")
            ];

            dnsRules =
            [
                new Dictionary<string, object?>
                {
                    ["domain_suffix"] = ruDomains,
                    ["server"] = "dns-local"
                },
                new Dictionary<string, object?>
                {
                    ["rule_set"] = new[] { "geosite-category-ru", "geosite-category-gov-ru" },
                    ["server"] = "dns-local"
                }
            ];
        }
        else if (routing.Mode == RoutingMode.AppProxy)
        {
            // Only listed apps via VPN — needs TUN + find_process
            finalOutbound = "direct";
            dnsFinal = "dns-local";
            if (routing.Processes.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["process_name"] = routing.Processes.ToArray(),
                    ["outbound"] = "proxy"
                });
            }
        }
        else if (routing.Mode == RoutingMode.AppBypass)
        {
            finalOutbound = "proxy";
            dnsFinal = "dns-remote";
            if (routing.Processes.Count > 0)
            {
                rules.Add(new Dictionary<string, object?>
                {
                    ["process_name"] = routing.Processes.ToArray(),
                    ["outbound"] = "direct"
                });
            }
        }

        // UDP DNS over VLESS/Reality often stalls (~1 min) — use DoH over the proxy instead.
        var dnsRulesList = new List<object>();
        if (!string.IsNullOrWhiteSpace(server.Host) && !IPAddressLooksLiteral(server.Host))
        {
            dnsRulesList.Add(new Dictionary<string, object?>
            {
                ["domain"] = new[] { server.Host },
                ["server"] = "dns-local"
            });
        }

        if (dnsRules is not null)
            dnsRulesList.AddRange(dnsRules);

        var dns = new Dictionary<string, object?>
        {
            ["servers"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "https",
                    ["tag"] = "dns-remote",
                    ["server"] = "1.1.1.1",
                    ["detour"] = "proxy"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "local",
                    ["tag"] = "dns-local"
                }
            },
            ["final"] = dnsFinal,
            ["strategy"] = "prefer_ipv4"
        };
        if (dnsRulesList.Count > 0)
            dns["rules"] = dnsRulesList;

        var route = new Dictionary<string, object?>
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "dns-local",
            ["rules"] = rules,
            ["final"] = finalOutbound
        };
        if (ruleSets is not null)
            route["rule_set"] = ruleSets;
        if (routing.Mode is RoutingMode.AppProxy or RoutingMode.AppBypass)
            route["find_process"] = true;

        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "data", "cache.db");

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["level"] = "info",
                ["timestamp"] = true
            },
            ["dns"] = dns,
            ["inbounds"] = inbounds,
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = route,
            ["experimental"] = new Dictionary<string, object?>
            {
                ["cache_file"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["path"] = cachePath
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object?> RemoteRuleSet(string tag, string url) =>
        new()
        {
            ["tag"] = tag,
            ["type"] = "remote",
            ["format"] = "binary",
            ["url"] = url,
            ["download_detour"] = "direct",
            ["update_interval"] = "24h"
        };

    /// <summary>Fallback RU domains (gov / banks / major services) if rule-set is not ready yet.</summary>
    private static string[] GetBuiltInRussianDomains() =>
    [
        "gov.ru", "gosuslugi.ru", "mos.ru", "nalog.ru", "cbr.ru", "kremlin.ru",
        "roskazna.ru", "zakupki.gov.ru", "bus.gov.ru", "sfr.gov.ru", "pfr.gov.ru",
        "fssp.gov.ru", "gu.spb.ru", "edu.ru", "minzdrav.gov.ru", "culture.gov.ru",
        "yandex.ru", "yandex.net", "ya.ru", "yandex.com",
        "mail.ru", "vk.com", "vk.ru", "ok.ru", "userapi.com",
        "sberbank.ru", "sber.ru", "tbank.ru", "tinkoff.ru", "vtb.ru", "alfabank.ru",
        "wildberries.ru", "ozon.ru", "avito.ru", "hh.ru", "2gis.ru",
        "rt.ru", "mts.ru", "megafon.ru", "beeline.ru", "tele2.ru",
        "ria.ru", "rbc.ru", "lenta.ru", "kp.ru"
    ];

    private static Dictionary<string, object?>? BuildOutbound(ServerProfile server)
    {
        var uri = server.RawUri;
        return server.Protocol switch
        {
            "vless" => BuildVless(uri),
            "trojan" => BuildTrojan(uri),
            "hysteria2" => BuildHysteria2(uri),
            "ss" => BuildShadowsocks(uri),
            "vmess" => BuildVmess(uri),
            _ => null
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.StartsWith('?') ? query[1..] : query;
        if (string.IsNullOrEmpty(q))
            return dict;
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

    private static string MapFingerprint(string? fp)
    {
        if (string.IsNullOrWhiteSpace(fp))
            return "chrome";
        var f = fp.Trim().ToLowerInvariant();
        // sing-box utls known values
        return f switch
        {
            "chrome" or "firefox" or "safari" or "ios" or "android" or "edge" or "360" or "qq" or "random" or "randomized"
                => f,
            _ => "chrome"
        };
    }

    private static Dictionary<string, object?> BuildVless(string uri)
    {
        var u = new Uri(uri);
        var q = ParseQuery(u.Query);
        var uuid = Uri.UnescapeDataString(u.UserInfo);
        var security = Q(q, "security") ?? "none";
        var sni = Q(q, "sni") ?? Q(q, "host") ?? u.IdnHost;
        var flow = Q(q, "flow");
        var pbk = Q(q, "pbk");
        var sidRaw = Q(q, "sid") ?? "";
        // Share links may list several short_ids: take the first
        var sid = sidRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var fp = MapFingerprint(Q(q, "fp"));
        var type = (Q(q, "type") ?? "tcp").ToLowerInvariant();
        var path = Q(q, "path") ?? "/";
        var hostHeader = Q(q, "host");
        var port = u.Port > 0 ? u.Port : 443;

        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = u.IdnHost,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["packet_encoding"] = "xudp"
        };
        // Vision flow is only valid on raw TCP
        if (!string.IsNullOrEmpty(flow) && type is "tcp" or "raw" or "")
            outbound["flow"] = flow;

        if (security is "tls" or "reality")
        {
            var tls = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni,
                ["utls"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["fingerprint"] = fp
                }
            };

            if (security == "reality")
            {
                if (string.IsNullOrWhiteSpace(pbk))
                    throw new InvalidOperationException("VLESS Reality: в ссылке нет pbk.");

                var reality = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["public_key"] = pbk,
                    ["short_id"] = sid
                };
                tls["reality"] = reality;
            }

            outbound["tls"] = tls;
        }

        if (type is "ws")
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "ws",
                ["path"] = path,
                ["headers"] = string.IsNullOrEmpty(hostHeader)
                    ? null
                    : new Dictionary<string, object?> { ["Host"] = hostHeader }
            };
        }
        else if (type is "grpc")
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "grpc",
                ["service_name"] = Q(q, "serviceName") ?? Q(q, "service_name") ?? ""
            };
        }
        else if (type is "xhttp" or "splithttp")
        {
            // Vision flow is TCP-only — drop it for xhttp
            outbound.Remove("flow");
            var mode = Q(q, "mode") ?? "auto";
            var transport = new Dictionary<string, object?>
            {
                ["type"] = "xhttp",
                ["path"] = path,
                ["mode"] = mode
            };
            if (!string.IsNullOrEmpty(hostHeader))
                transport["host"] = hostHeader;
            outbound["transport"] = transport;
        }

        return outbound;
    }

    private static bool IPAddressLooksLiteral(string host) =>
        System.Net.IPAddress.TryParse(host, out _);

    private static Dictionary<string, object?> BuildTrojan(string uri)
    {
        var u = new Uri(uri);
        var q = ParseQuery(u.Query);
        var sni = Q(q, "sni") ?? u.IdnHost;
        return new Dictionary<string, object?>
        {
            ["type"] = "trojan",
            ["tag"] = "proxy",
            ["server"] = u.IdnHost,
            ["server_port"] = u.Port > 0 ? u.Port : 443,
            ["password"] = Uri.UnescapeDataString(u.UserInfo),
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni
            }
        };
    }

    private static Dictionary<string, object?> BuildHysteria2(string uri)
    {
        var normalized = uri.Replace("hysteria2://", "hy2://", StringComparison.OrdinalIgnoreCase);
        var u = new Uri(normalized.Replace("hy2://", "https://", StringComparison.OrdinalIgnoreCase));
        var q = ParseQuery(u.Query);
        var password = Uri.UnescapeDataString(u.UserInfo);
        var sni = Q(q, "sni") ?? u.IdnHost;
        var insecure = Q(q, "insecure");
        return new Dictionary<string, object?>
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = u.IdnHost,
            ["server_port"] = u.Port == -1 ? 443 : u.Port,
            ["password"] = password,
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni,
                ["insecure"] = insecure is "1" or "true"
            }
        };
    }

    private static Dictionary<string, object?> BuildShadowsocks(string uri)
    {
        var withoutScheme = uri["ss://".Length..];
        var hash = withoutScheme.IndexOf('#');
        if (hash >= 0)
            withoutScheme = withoutScheme[..hash];

        string method, password, host;
        int port;

        if (withoutScheme.Contains('@'))
        {
            var at = withoutScheme.LastIndexOf('@');
            var userInfo = withoutScheme[..at];
            var hostPort = withoutScheme[(at + 1)..];
            string decodedUser;
            try
            {
                var pad = userInfo.Length % 4;
                var b64 = pad == 0 ? userInfo : userInfo.PadRight(userInfo.Length + (4 - pad), '=');
                decodedUser = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/')));
            }
            catch
            {
                decodedUser = Uri.UnescapeDataString(userInfo);
            }

            var colon = decodedUser.IndexOf(':');
            method = decodedUser[..colon];
            password = decodedUser[(colon + 1)..];
            var hp = hostPort.Split(':');
            host = hp[0];
            port = int.Parse(hp[1]);
        }
        else
        {
            throw new InvalidOperationException("Формат ss:// не распознан.");
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "shadowsocks",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password
        };
    }

    private static Dictionary<string, object?> BuildVmess(string uri)
    {
        var b64 = uri["vmess://".Length..];
        var pad = b64.Length % 4;
        if (pad != 0)
            b64 = b64.PadRight(b64.Length + (4 - pad), '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        var root = doc.RootElement;
        string Get(string k) => root.TryGetProperty(k, out var e) ? e.ToString() : "";

        var host = Get("add");
        var port = int.TryParse(Get("port"), out var p) ? p : 0;
        var uuid = Get("id");
        var aid = int.TryParse(Get("aid"), out var a) ? a : 0;
        var net = string.IsNullOrEmpty(Get("net")) ? "tcp" : Get("net");
        var tls = Get("tls");
        var sni = string.IsNullOrEmpty(Get("sni")) ? host : Get("sni");
        var path = string.IsNullOrEmpty(Get("path")) ? "/" : Get("path");
        var hostHeader = Get("host");

        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "vmess",
            ["tag"] = "proxy",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["alter_id"] = aid,
            ["security"] = string.IsNullOrEmpty(Get("scy")) ? "auto" : Get("scy")
        };

        if (tls is "tls")
        {
            outbound["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni
            };
        }

        if (net == "ws")
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "ws",
                ["path"] = path,
                ["headers"] = string.IsNullOrEmpty(hostHeader)
                    ? null
                    : new Dictionary<string, object?> { ["Host"] = hostHeader }
            };
        }

        return outbound;
    }
}
