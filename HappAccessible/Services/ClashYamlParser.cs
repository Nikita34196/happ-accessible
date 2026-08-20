using System.Text;
using System.Text.RegularExpressions;
using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>
/// Lightweight Clash / Clash Meta <c>proxies:</c> extractor without a YAML library.
/// Converts common proxy entries into share-link URIs for <see cref="SubscriptionParser"/>.
/// </summary>
public static class ClashYamlParser
{
    public static bool LooksLikeClash(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var t = content.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
            return false;
        return Regex.IsMatch(content, @"(?m)^\s*proxies\s*:", RegexOptions.IgnoreCase)
               || (content.Contains("type:", StringComparison.OrdinalIgnoreCase)
                   && content.Contains("server:", StringComparison.OrdinalIgnoreCase)
                   && (content.Contains("mixed-port:", StringComparison.OrdinalIgnoreCase)
                       || content.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<ServerProfile> Parse(string content)
    {
        if (!LooksLikeClash(content))
            return [];

        var proxies = ExtractProxyBlocks(content);
        var result = new List<ServerProfile>();
        foreach (var block in proxies)
        {
            var uri = TryBuildUri(block);
            if (uri is null)
                continue;
            var profile = SubscriptionParser.ParseLinePublic(uri);
            if (profile is not null && !SubscriptionParser.IsDummyStub(profile))
                result.Add(profile);
        }

        return result;
    }

    private static List<Dictionary<string, string>> ExtractProxyBlocks(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inProxies = false;
        var proxiesIndent = -1;
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var currentIndent = 0;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
                continue;

            var indent = raw.TakeWhile(c => c is ' ' or '\t').Count();
            var line = raw.Trim();

            if (!inProxies)
            {
                if (Regex.IsMatch(line, @"^proxies\s*:\s*$", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(line, @"^proxies\s*:\s*\[", RegexOptions.IgnoreCase))
                {
                    inProxies = true;
                    proxiesIndent = indent;
                }
                continue;
            }

            // Left proxies section
            if (indent <= proxiesIndent && !line.StartsWith('-') && Regex.IsMatch(line, @"^[A-Za-z0-9_-]+\s*:"))
            {
                if (current is not null)
                    blocks.Add(current);
                break;
            }

            // Inline flow style: - { name: x, type: ss, ... }
            if (line.StartsWith('-'))
            {
                if (current is not null)
                    blocks.Add(current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                currentIndent = indent;
                var rest = line[1..].Trim();
                if (rest.StartsWith('{') && rest.EndsWith('}'))
                {
                    ParseFlowMap(rest[1..^1], current);
                    blocks.Add(current);
                    current = null;
                }
                else if (rest.Contains(':'))
                {
                    // - name: foo
                    var kv = SplitKv(rest);
                    if (kv is not null)
                        current[kv.Value.Key] = kv.Value.Value;
                }
                continue;
            }

            if (current is null)
                continue;

            if (indent <= currentIndent && line.StartsWith('-'))
            {
                blocks.Add(current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                currentIndent = indent;
            }

            var pair = SplitKv(line);
            if (pair is not null)
                current[pair.Value.Key] = pair.Value.Value;
        }

        if (current is not null)
            blocks.Add(current);

        return blocks;
    }

    private static void ParseFlowMap(string inner, Dictionary<string, string> target)
    {
        foreach (var part in SplitFlowParts(inner))
        {
            var kv = SplitKv(part);
            if (kv is not null)
                target[kv.Value.Key] = kv.Value.Value;
        }
    }

    private static IEnumerable<string> SplitFlowParts(string inner)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        char quote = '\0';
        foreach (var ch in inner)
        {
            if (inQuote)
            {
                sb.Append(ch);
                if (ch == quote)
                    inQuote = false;
                continue;
            }

            if (ch is '"' or '\'')
            {
                inQuote = true;
                quote = ch;
                sb.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                var part = sb.ToString().Trim();
                if (part.Length > 0)
                    yield return part;
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        var last = sb.ToString().Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static (string Key, string Value)? SplitKv(string line)
    {
        var i = line.IndexOf(':');
        if (i <= 0)
            return null;
        var key = line[..i].Trim();
        var val = line[(i + 1)..].Trim();
        if (val.StartsWith('"') && val.EndsWith('"') && val.Length >= 2)
            val = val[1..^1];
        else if (val.StartsWith('\'') && val.EndsWith('\'') && val.Length >= 2)
            val = val[1..^1];
        return (key, val);
    }

    private static string? TryBuildUri(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("type", out var type) || !p.TryGetValue("server", out var server))
            return null;
        if (!p.TryGetValue("port", out var portStr) || !int.TryParse(portStr, out var port))
            return null;

        var name = p.TryGetValue("name", out var n) ? Uri.EscapeDataString(n) : "clash";
        type = type.Trim().ToLowerInvariant();

        return type switch
        {
            "ss" or "shadowsocks" => BuildSs(p, server, port, name),
            "trojan" => BuildTrojan(p, server, port, name),
            "vmess" => BuildVmess(p, server, port, name),
            "vless" => BuildVless(p, server, port, name),
            "hysteria2" or "hy2" => BuildHy2(p, server, port, name),
            "hysteria" => BuildHy1(p, server, port, name),
            "wireguard" => BuildWg(p, server, port, name),
            _ => null
        };
    }

    private static string? BuildSs(Dictionary<string, string> p, string server, int port, string name)
    {
        var method = Get(p, "cipher") ?? Get(p, "method");
        var password = Get(p, "password");
        if (method is null || password is null)
            return null;
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"))
            .TrimEnd('=');
        return $"ss://{userInfo}@{server}:{port}#{name}";
    }

    private static string? BuildTrojan(Dictionary<string, string> p, string server, int port, string name)
    {
        var password = Get(p, "password");
        if (password is null)
            return null;
        var sni = Get(p, "sni") ?? Get(p, "servername") ?? server;
        return $"trojan://{Uri.EscapeDataString(password)}@{server}:{port}?sni={Uri.EscapeDataString(sni)}#{name}";
    }

    private static string? BuildVmess(Dictionary<string, string> p, string server, int port, string name)
    {
        var uuid = Get(p, "uuid") ?? Get(p, "id");
        if (uuid is null)
            return null;
        var json = $"{{\"v\":\"2\",\"ps\":\"{EscapeJson(Get(p, "name") ?? "vmess")}\",\"add\":\"{EscapeJson(server)}\",\"port\":\"{port}\",\"id\":\"{EscapeJson(uuid)}\",\"aid\":\"{Get(p, "alterId") ?? "0"}\",\"scy\":\"{Get(p, "cipher") ?? "auto"}\",\"net\":\"{Get(p, "network") ?? "tcp"}\",\"type\":\"none\",\"host\":\"{EscapeJson(Get(p, "host") ?? "")}\",\"path\":\"{EscapeJson(Get(p, "path") ?? "/")}\",\"tls\":\"{(Get(p, "tls") is "true" or "1" ? "tls" : "")}\",\"sni\":\"{EscapeJson(Get(p, "servername") ?? Get(p, "sni") ?? "")}\"}}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"vmess://{b64}";
    }

    private static string? BuildVless(Dictionary<string, string> p, string server, int port, string name)
    {
        var uuid = Get(p, "uuid") ?? Get(p, "id");
        if (uuid is null)
            return null;
        var q = new List<string>();
        var security = Get(p, "tls") is "true" or "1" ? "tls" : (Get(p, "reality-opts") is not null ? "reality" : "none");
        if (Get(p, "client-fingerprint") is { } fp)
            q.Add("fp=" + Uri.EscapeDataString(fp));
        var sniVal = Get(p, "servername") ?? Get(p, "sni");
        if (sniVal is not null)
            q.Add("sni=" + Uri.EscapeDataString(sniVal));
        if (Get(p, "flow") is { } flow)
            q.Add("flow=" + Uri.EscapeDataString(flow));
        if (Get(p, "network") is { } net)
            q.Add("type=" + Uri.EscapeDataString(net));
        if (Get(p, "path") is { } path)
            q.Add("path=" + Uri.EscapeDataString(path));
        if (Get(p, "host") is { } host)
            q.Add("host=" + Uri.EscapeDataString(host));
        // Reality public key often under reality-opts.public-key — skip nested for lite parser
        if (Get(p, "public-key") is { } pbk)
        {
            security = "reality";
            q.Add("pbk=" + Uri.EscapeDataString(pbk));
        }
        if (Get(p, "short-id") is { } sid)
            q.Add("sid=" + Uri.EscapeDataString(sid));
        q.Insert(0, "security=" + security);
        return $"vless://{uuid}@{server}:{port}?{string.Join("&", q)}#{name}";
    }

    private static string? BuildHy2(Dictionary<string, string> p, string server, int port, string name)
    {
        var password = Get(p, "password") ?? Get(p, "auth");
        if (password is null)
            return null;
        var sni = Get(p, "sni") ?? Get(p, "servername") ?? server;
        var insecure = Get(p, "skip-cert-verify") is "true" or "1" ? "1" : "0";
        return $"hysteria2://{Uri.EscapeDataString(password)}@{server}:{port}?sni={Uri.EscapeDataString(sni)}&insecure={insecure}#{name}";
    }

    private static string? BuildHy1(Dictionary<string, string> p, string server, int port, string name)
    {
        var auth = Get(p, "auth_str") ?? Get(p, "auth-str") ?? Get(p, "auth") ?? Get(p, "password");
        if (auth is null)
            return null;
        var peer = Get(p, "sni") ?? Get(p, "servername") ?? server;
        var insecure = Get(p, "skip-cert-verify") is "true" or "1" ? "1" : "0";
        var up = Get(p, "up") ?? Get(p, "upmbps") ?? "50";
        var down = Get(p, "down") ?? Get(p, "downmbps") ?? "200";
        return $"hysteria://{server}:{port}?auth={Uri.EscapeDataString(auth)}&peer={Uri.EscapeDataString(peer)}&insecure={insecure}&upmbps={up}&downmbps={down}#{name}";
    }

    private static string? BuildWg(Dictionary<string, string> p, string server, int port, string name)
    {
        var privateKey = Get(p, "private-key") ?? Get(p, "privateKey");
        var publicKey = Get(p, "public-key") ?? Get(p, "publicKey");
        if (privateKey is null || publicKey is null)
            return null;
        var ip = Get(p, "ip") ?? Get(p, "address") ?? "10.0.0.2/32";
        return $"wireguard://{Uri.EscapeDataString(privateKey)}@{server}:{port}?publickey={Uri.EscapeDataString(publicKey)}&address={Uri.EscapeDataString(ip)}#{name}";
    }

    private static string? Get(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
