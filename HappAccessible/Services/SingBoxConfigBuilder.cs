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
        var routingProfile = engine.RoutingProfile;
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
            // QUIC (UDP/443) через VLESS+xhttp часто зависает — Google/YouTube в Chrome
            // не открываются, пока браузер не упадёт на TCP HTTPS. Telegram на TCP ок.
        };
        // TCP Reality/Vision supports UDP via xudp; rejecting QUIC there adds
        // a fallback delay to every HTTP/3-capable browser. Keep the reject
        // workaround only for xhttp/splithttp, where UDP is known to stall.
        if (engine.RejectQuicUdp443 && UsesUnreliableQuicTransport(server))
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["protocol"] = "quic",
                ["action"] = "reject"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["network"] = "udp",
                ["port"] = 443,
                ["action"] = "reject"
            });
        }

        rules.Add(new Dictionary<string, object?>
        {
            ["ip_is_private"] = true,
            ["outbound"] = "direct"
        });

        rules.Add(new Dictionary<string, object?>
        {
            ["domain_suffix"] = DirectHttp.GitHubDomainSuffixes,
            ["outbound"] = "direct"
        });

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
            AppendDomainAndRuleSetRules(routing.Domains, outbound: "proxy", rules, ref ruleSets, ref dnsRules, dnsServer: "dns-remote");
        }
        else if (routing.Mode == RoutingMode.BypassList)
        {
            finalOutbound = "proxy";
            dnsFinal = "dns-remote";
            AppendDomainAndRuleSetRules(routing.Domains, outbound: "direct", rules, ref ruleSets, ref dnsRules, dnsServer: null);
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

        if (routingProfile is not null)
            ApplyHappProfileRules(routingProfile, rules, ref ruleSets, ref dnsRules, ref finalOutbound);

        // UDP DNS over VLESS/Reality often stalls (~1 min) — use DoH/DoU over the proxy instead.
        var dns = BuildDnsSection(server, engine, enableTun, dnsFinal, dnsRules, out var usesFakeDns);
        if (usesFakeDns)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["ip_cidr"] = new[] { "198.18.0.0/15" },
                ["outbound"] = "proxy"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["ip_cidr"] = new[] { "fc00::/18" },
                ["outbound"] = "proxy"
            });
        }

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

        if (ruleSets is not null)
            route["default_http_client"] = "hc-direct";

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "data");

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                // Per-connection info logging is expensive in TUN mode and
                // can throttle traffic because the runner persists each line.
                ["level"] = "warn",
                ["timestamp"] = true
            },
            ["experimental"] = new Dictionary<string, object?>
            {
                ["cache_file"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["path"] = Path.Combine(dataDir, "cache.db")
                }
            },
            ["http_clients"] = new object[]
            {
                new Dictionary<string, object?> { ["tag"] = "hc-direct" }
            },
            ["dns"] = dns,
            ["inbounds"] = inbounds,
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = route
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AppendDomainAndRuleSetRules(
        IReadOnlyList<string> entries,
        string outbound,
        List<object> rules,
        ref List<object>? ruleSets,
        ref List<object>? dnsRules,
        string? dnsServer)
    {
        var (domains, geosite, geoip) = RoutingOptions.SplitRoutingTags(entries);
        if (domains.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["domain_suffix"] = domains.ToArray(),
                ["outbound"] = outbound
            });
            if (dnsServer is not null)
            {
                dnsRules ??= [];
                dnsRules.Add(new Dictionary<string, object?>
                {
                    ["domain_suffix"] = domains.ToArray(),
                    ["server"] = dnsServer
                });
            }
        }

        ruleSets ??= [];
        var geositeTags = new List<string>();
        foreach (var tag in geosite)
        {
            var rsTag = "geosite-" + tag.Replace(':', '-');
            geositeTags.Add(rsTag);
            if (ruleSets.All(r => r is not Dictionary<string, object?> d || !Equals(d.GetValueOrDefault("tag"), rsTag)))
            {
                ruleSets.Add(RemoteRuleSet(rsTag,
                    $"https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{tag}.srs"));
            }
        }

        var geoipTags = new List<string>();
        foreach (var tag in geoip)
        {
            if (IsBuiltInGeoIpTag(tag))
                continue;

            var rsTag = "geoip-" + tag.Replace(':', '-');
            geoipTags.Add(rsTag);
            if (ruleSets.All(r => r is not Dictionary<string, object?> d || !Equals(d.GetValueOrDefault("tag"), rsTag)))
            {
                ruleSets.Add(RemoteRuleSet(rsTag,
                    $"https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{tag}.srs"));
            }
        }

        if (geositeTags.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = geositeTags.ToArray(),
                ["outbound"] = outbound
            });
            if (dnsServer is not null)
            {
                dnsRules ??= [];
                dnsRules.Add(new Dictionary<string, object?>
                {
                    ["rule_set"] = geositeTags.ToArray(),
                    ["server"] = dnsServer
                });
            }
        }

        if (geoipTags.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = geoipTags.Count == 1 ? geoipTags[0] : geoipTags.ToArray(),
                ["outbound"] = outbound
            });
        }

        if (ruleSets.Count == 0)
            ruleSets = null;
    }

    /// sing-geoip has no geoip-private.srs; private ranges are matched by ip_is_private in route rules.
    private static bool IsBuiltInGeoIpTag(string tag) =>
        string.Equals(tag, "private", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> RemoteRuleSet(string tag, string url) =>
        new()
        {
            ["tag"] = tag,
            ["type"] = "remote",
            ["format"] = "binary",
            ["url"] = url,
            ["update_interval"] = "24h"
        };

    private static void ApplyHappProfileRules(
        HappRoutingProfile profile,
        List<object> rules,
        ref List<object>? ruleSets,
        ref List<object>? dnsRules,
        ref string finalOutbound)
    {
        AppendRejectRules(profile.BlockSites, rules, ref ruleSets);
        AppendRejectRules(profile.BlockIp, rules, ref ruleSets);
        AppendDomainAndRuleSetRules(profile.DirectSites, "direct", rules, ref ruleSets, ref dnsRules, "dns-domestic");
        AppendDomainAndRuleSetRules(profile.DirectIp, "direct", rules, ref ruleSets, ref dnsRules, "dns-domestic");
        AppendDomainAndRuleSetRules(profile.ProxySites, "proxy", rules, ref ruleSets, ref dnsRules, "dns-remote");
        AppendDomainAndRuleSetRules(profile.ProxyIp, "proxy", rules, ref ruleSets, ref dnsRules, "dns-remote");

        if (profile.GlobalProxy)
            finalOutbound = "proxy";
    }

    private static void AppendRejectRules(
        IReadOnlyList<string> entries,
        List<object> rules,
        ref List<object>? ruleSets)
    {
        if (entries.Count == 0)
            return;

        var (domains, geosite, geoip) = RoutingOptions.SplitRoutingTags(entries);
        if (domains.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["domain"] = domains.ToArray(),
                ["action"] = "reject"
            });
        }

        ruleSets ??= [];
        var geositeTags = new List<string>();
        foreach (var tag in geosite)
        {
            var rsTag = "geosite-" + tag.Replace(':', '-');
            geositeTags.Add(rsTag);
            if (ruleSets.All(r => r is not Dictionary<string, object?> d || !Equals(d.GetValueOrDefault("tag"), rsTag)))
            {
                ruleSets.Add(RemoteRuleSet(rsTag,
                    $"https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{tag}.srs"));
            }
        }

        var geoipTags = new List<string>();
        foreach (var tag in geoip)
        {
            if (IsBuiltInGeoIpTag(tag))
                continue;

            var rsTag = "geoip-" + tag.Replace(':', '-');
            geoipTags.Add(rsTag);
            if (ruleSets.All(r => r is not Dictionary<string, object?> d || !Equals(d.GetValueOrDefault("tag"), rsTag)))
            {
                ruleSets.Add(RemoteRuleSet(rsTag,
                    $"https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{tag}.srs"));
            }
        }

        if (geositeTags.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = geositeTags.ToArray(),
                ["action"] = "reject"
            });
        }

        if (geoipTags.Count > 0)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["rule_set"] = geoipTags.Count == 1 ? geoipTags[0] : geoipTags.ToArray(),
                ["action"] = "reject"
            });
        }

        if (ruleSets.Count == 0)
            ruleSets = null;
    }

    private static Dictionary<string, object?> BuildDnsSection(
        ServerProfile server,
        EngineOptions engine,
        bool enableTun,
        string dnsFinal,
        List<object>? routingDnsRules,
        out bool usesFakeDns)
    {
        usesFakeDns = engine.FakeDns && enableTun;
        var servers = new List<object>();
        var rules = new List<object>();

        if (!string.IsNullOrWhiteSpace(server.Host) && !IPAddressLooksLiteral(server.Host))
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["domain"] = new[] { server.Host },
                ["server"] = "dns-local"
            });
        }

        rules.Add(new Dictionary<string, object?>
        {
            ["domain_suffix"] = DirectHttp.GitHubDomainSuffixes,
            ["server"] = "dns-local"
        });

        if (routingDnsRules is not null)
            rules.AddRange(routingDnsRules);

        servers.Add(BuildDnsServer(
            "dns-remote",
            engine.DnsRemoteType,
            engine.DnsRemoteServer,
            engine.DnsRemoteDomain,
            "proxy"));

        var fallback = NormalizeDnsHost(engine.DnsRemoteFallback, "8.8.8.8");
        if (!string.Equals(fallback, engine.DnsRemoteServer, StringComparison.OrdinalIgnoreCase))
        {
            servers.Add(BuildDnsServer(
                "dns-remote-fallback",
                "DoH",
                fallback,
                "",
                "proxy"));
        }

        if (!string.IsNullOrWhiteSpace(engine.DnsDomesticServer))
        {
            servers.Add(BuildDnsServer(
                "dns-domestic",
                engine.DnsDomesticType,
                engine.DnsDomesticServer,
                engine.DnsDomesticDomain,
                "direct"));
        }

        servers.Add(new Dictionary<string, object?>
        {
            ["type"] = "local",
            ["tag"] = "dns-local"
        });

        var predefinedHosts = BuildPredefinedHosts(engine.DnsHosts);
        if (predefinedHosts.Count > 0)
        {
            servers.Add(new Dictionary<string, object?>
            {
                ["type"] = "hosts",
                ["tag"] = "dns-hosts",
                ["path"] = Array.Empty<string>(),
                ["predefined"] = predefinedHosts
            });
            rules.Insert(0, new Dictionary<string, object?>
            {
                ["preferred_by"] = "dns-hosts",
                ["action"] = "route",
                ["server"] = "dns-hosts"
            });
        }

        if (usesFakeDns)
        {
            servers.Add(new Dictionary<string, object?>
            {
                ["type"] = "fakeip",
                ["tag"] = "fakeip",
                ["inet4_range"] = "198.18.0.0/15",
                ["inet6_range"] = "fc00::/18"
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["query_type"] = new[] { "A", "AAAA" },
                ["server"] = "fakeip"
            });
        }

        var dns = new Dictionary<string, object?>
        {
            ["servers"] = servers,
            ["final"] = usesFakeDns ? "dns-remote" : dnsFinal,
            ["strategy"] = NormalizeDnsStrategy(engine.DnsStrategy)
        };
        if (usesFakeDns)
            dns["independent_cache"] = true;
        if (rules.Count > 0)
            dns["rules"] = rules;

        return dns;
    }

    private static Dictionary<string, object?> BuildPredefinedHosts(IReadOnlyDictionary<string, string> dnsHosts)
    {
        var predefined = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, ip) in dnsHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(ip))
                continue;
            predefined[host.Trim()] = ip.Trim();
        }

        return predefined;
    }

    private static Dictionary<string, object?> BuildDnsServer(
        string tag,
        string dnsType,
        string serverHost,
        string domainUrl,
        string detour)
    {
        var (host, path, sni) = ParseDnsEndpoint(serverHost, domainUrl);
        var useDetour = !string.Equals(detour, "direct", StringComparison.OrdinalIgnoreCase);

        Dictionary<string, object?> WithDetour(Dictionary<string, object?> server)
        {
            if (useDetour && !string.IsNullOrWhiteSpace(detour))
                server["detour"] = detour;
            return server;
        }

        return dnsType.Trim().ToUpperInvariant() switch
        {
            "DOU" => WithDetour(new Dictionary<string, object?>
            {
                ["type"] = "udp",
                ["tag"] = tag,
                ["server"] = host
            }),
            "DOT" => WithDetour(new Dictionary<string, object?>
            {
                ["type"] = "tls",
                ["tag"] = tag,
                ["server"] = host,
                ["tls"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["server_name"] = sni ?? host
                }
            }),
            _ => WithDetour(new Dictionary<string, object?>
            {
                ["type"] = "https",
                ["tag"] = tag,
                ["server"] = host,
                ["path"] = path,
                ["tls"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["server_name"] = sni ?? DnsTlsServerName(host, "cloudflare-dns.com")
                }
            })
        };
    }

    private static (string Host, string Path, string? Sni) ParseDnsEndpoint(string serverHost, string domainUrl)
    {
        domainUrl = (domainUrl ?? "").Trim();
        if (!string.IsNullOrEmpty(domainUrl)
            && Uri.TryCreate(domainUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/dns-query" : uri.AbsolutePath;
            return (uri.Host, path, uri.Host);
        }

        return (NormalizeDnsHost(serverHost, "1.1.1.1"), "/dns-query", null);
    }

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
            "hysteria" => BuildHysteria1(uri),
            "wireguard" => BuildWireGuard(uri),
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
            ["uuid"] = uuid
        };
        // Vision flow is only valid on raw TCP
        if (!string.IsNullOrEmpty(flow) && type is "tcp" or "raw" or "")
            outbound["flow"] = flow;
        // Vision is TCP-only; xudp is for UDP and can interfere with first connect checks.
        if (type is not ("xhttp" or "splithttp")
            && !string.Equals(flow, "xtls-rprx-vision", StringComparison.OrdinalIgnoreCase)
            && flow?.Contains("xtls", StringComparison.OrdinalIgnoreCase) != true)
            outbound["packet_encoding"] = "xudp";


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

            var alpnRaw = Q(q, "alpn");
            if (!string.IsNullOrWhiteSpace(alpnRaw))
            {
                var alpns = alpnRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (alpns.Length > 0)
                    tls["alpn"] = alpns;
            }

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
            // Vision flow is TCP-only — drop it for xhttp (sing-box-lx supports type=xhttp)
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

    private static bool UsesUnreliableQuicTransport(ServerProfile server)
    {
        if (server.Protocol is not ("vless" or "trojan" or "vmess"))
            return false;

        try
        {
            var queryStart = server.RawUri.IndexOf('?');
            if (queryStart < 0)
                return false;
            var queryEnd = server.RawUri.IndexOf('#', queryStart);
            var query = queryEnd >= 0
                ? server.RawUri[queryStart..queryEnd]
                : server.RawUri[queryStart..];
            var type = Q(ParseQuery(query), "type");
            return string.Equals(type, "xhttp", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(type, "splithttp", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> BuildTrojan(string uri)
    {
        var u = new Uri(uri);
        var q = ParseQuery(u.Query);
        var sni = Q(q, "sni") ?? u.IdnHost;
        var outbound = new Dictionary<string, object?>
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
        var type = (Q(q, "type") ?? "tcp").ToLowerInvariant();
        if (type == "ws")
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "ws",
                ["path"] = Q(q, "path") ?? "/",
                ["headers"] = new Dictionary<string, object?> { ["Host"] = Q(q, "host") ?? sni }
            };
        }
        else if (type == "grpc")
        {
            outbound["transport"] = new Dictionary<string, object?>
            {
                ["type"] = "grpc",
                ["service_name"] = Q(q, "serviceName") ?? Q(q, "service") ?? ""
            };
        }

        return outbound;
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

    private static Dictionary<string, object?> BuildHysteria1(string uri)
    {
        // hysteria://host:port?auth=...&peer=sni&insecure=1&upmbps=&downmbps=
        var normalized = uri.Replace("hysteria://", "https://", StringComparison.OrdinalIgnoreCase);
        var hash = normalized.IndexOf('#');
        if (hash >= 0)
            normalized = normalized[..hash];
        var u = new Uri(normalized);
        var q = ParseQuery(u.Query);
        var auth = Q(q, "auth") ?? Q(q, "auth_str") ?? Uri.UnescapeDataString(u.UserInfo);
        if (string.IsNullOrWhiteSpace(auth))
            throw new InvalidOperationException("Hysteria: в ссылке нет auth.");
        var sni = Q(q, "peer") ?? Q(q, "sni") ?? u.IdnHost;
        _ = int.TryParse(Q(q, "upmbps") ?? "50", out var up);
        _ = int.TryParse(Q(q, "downmbps") ?? "200", out var down);
        return new Dictionary<string, object?>
        {
            ["type"] = "hysteria",
            ["tag"] = "proxy",
            ["server"] = u.IdnHost,
            ["server_port"] = u.Port == -1 ? 443 : u.Port,
            ["up_mbps"] = Math.Max(1, up),
            ["down_mbps"] = Math.Max(1, down),
            ["auth_str"] = auth,
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni,
                ["insecure"] = Q(q, "insecure") is "1" or "true"
            }
        };
    }

    private static Dictionary<string, object?> BuildWireGuard(string uri)
    {
        // wireguard://PRIVATEKEY@server:port?publickey=...&address=10.0.0.2/32&mtu=...&reserved=...
        var withoutScheme = uri;
        if (withoutScheme.StartsWith("wg://", StringComparison.OrdinalIgnoreCase))
            withoutScheme = "wireguard://" + withoutScheme["wg://".Length..];
        var hash = withoutScheme.IndexOf('#');
        if (hash >= 0)
            withoutScheme = withoutScheme[..hash];

        var asHttps = withoutScheme.Replace("wireguard://", "https://", StringComparison.OrdinalIgnoreCase);
        var u = new Uri(asHttps);
        var q = ParseQuery(u.Query);
        var privateKey = Uri.UnescapeDataString(u.UserInfo);
        var publicKey = Q(q, "publickey") ?? Q(q, "publicKey") ?? Q(q, "peerpublickey");
        if (string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidOperationException("WireGuard: нужны private key и publickey.");

        var address = Q(q, "address") ?? Q(q, "ip") ?? "10.0.0.2/32";
        var localAddresses = address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _ = int.TryParse(Q(q, "mtu") ?? "1420", out var mtu);

        var peer = new Dictionary<string, object?>
        {
            ["server"] = u.IdnHost,
            ["server_port"] = u.Port == -1 ? 51820 : u.Port,
            ["public_key"] = publicKey,
            ["allowed_ips"] = new[] { "0.0.0.0/0", "::/0" }
        };
        var psk = Q(q, "presharedkey") ?? Q(q, "psk");
        if (psk is not null)
            peer["pre_shared_key"] = psk;
        if (Q(q, "reserved") is { } reserved)
        {
            var bytes = reserved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => byte.TryParse(s, out var b) ? b : (byte)0)
                .ToArray();
            if (bytes.Length > 0)
                peer["reserved"] = bytes;
        }

        // AmneziaWG obfuscation params (Jc/Jmin/Jmax/S1/S2/H1-H4) → use AmneziaWG path instead when present
        return new Dictionary<string, object?>
        {
            ["type"] = "wireguard",
            ["tag"] = "proxy",
            ["private_key"] = privateKey,
            ["local_address"] = localAddresses,
            ["mtu"] = Math.Clamp(mtu, 1280, 1500),
            ["peers"] = new object[] { peer }
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
            (host, port) = SplitHostPort(hostPort);
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

    private static (string Host, int Port) SplitHostPort(string value)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end <= 1 || end + 2 > value.Length || value[end + 1] != ':'
                || !int.TryParse(value[(end + 2)..], out var bracketPort))
                throw new FormatException("Некорректный IPv6 host:port.");
            return (value[1..end], bracketPort);
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out var port))
            throw new FormatException("Некорректный host:port.");
        return (value[..colon], port);
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

    public static void ClearDnsCache()
    {
        try
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HappAccessible", "data", "cache.db");
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch
        {
            // ignore
        }
    }

    private static string NormalizeDnsHost(string? host, string fallback)
    {
        host = (host ?? "").Trim();
        return host.Length > 0 ? host : fallback;
    }

    private static string DnsTlsServerName(string host, string fallback) =>
        host switch
        {
            "1.1.1.1" or "1.0.0.1" => "cloudflare-dns.com",
            "8.8.8.8" or "8.8.4.4" => "dns.google",
            _ when IPAddressLooksLiteral(host) => fallback,
            _ => host
        };

    private static string NormalizeDnsStrategy(string? strategy) =>
        (strategy ?? "").Trim().ToLowerInvariant() switch
        {
            "prefer_ipv4" => "prefer_ipv4",
            "prefer_ipv6" => "prefer_ipv6",
            "ipv6_only" => "ipv6_only",
            _ => "ipv4_only"
        };
}
