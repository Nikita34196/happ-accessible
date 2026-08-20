using System.Text;
using System.Text.RegularExpressions;
using HappAccessible.Models;

namespace HappAccessible.Services;

public static class SubscriptionParser
{
    private static readonly string[] Schemes =
    [
        "vless://", "vmess://", "trojan://", "ss://",
        "hysteria2://", "hy2://", "hysteria://",
        "wireguard://", "wg://"
    ];

    public static IReadOnlyList<ServerProfile> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var text = content.Trim();

        // Clash / Clash Meta YAML
        if (ClashYamlParser.LooksLikeClash(text))
        {
            var clash = ClashYamlParser.Parse(text);
            if (clash.Count > 0)
                return clash;
        }

        // Whole body may be base64 of newline-separated URIs
        if (!LooksLikeUriList(text))
        {
            var decoded = TryDecodeBase64(text);
            if (!string.IsNullOrWhiteSpace(decoded))
                text = decoded;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<ServerProfile>();
        foreach (var line in lines)
        {
            var profile = ParseLine(line);
            if (profile is not null && !IsDummyStub(profile))
                result.Add(profile);
        }

        return result;
    }

    /// <summary>Public entry for Clash converter and tests.</summary>
    public static ServerProfile? ParseLinePublic(string line) => ParseLine(line);

    /// <summary>
    /// Remnawave / Geodema returns a fake "App not supported" node when HWID is missing
    /// or the client User-Agent is blocked.
    /// </summary>
    public static bool IsDummyStub(ServerProfile profile)
    {
        if (profile.Name.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(profile.Host, "0.0.0.0", StringComparison.Ordinal)
            || string.Equals(profile.Host, "127.0.0.1", StringComparison.Ordinal))
            return true;
        if (profile.Port is 0 or 1
            && profile.RawUri.Contains("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool LooksLikeUriList(string text) =>
        Schemes.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string? TryDecodeBase64(string text)
    {
        try
        {
            var cleaned = Regex.Replace(text, @"\s+", "");
            var pad = cleaned.Length % 4;
            if (pad != 0)
                cleaned = cleaned.PadRight(cleaned.Length + (4 - pad), '=');

            var bytes = Convert.FromBase64String(cleaned);
            var s = Encoding.UTF8.GetString(bytes);
            return LooksLikeUriList(s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    private static ServerProfile? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            return null;

        var uri = line.Trim();
        if (!Schemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            if (uri.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                return ParseVmess(uri);

            var hash = uri.IndexOf('#');
            var name = hash >= 0 ? Uri.UnescapeDataString(uri[(hash + 1)..]) : null;
            var withoutHash = hash >= 0 ? uri[..hash] : uri;

            var schemeEnd = withoutHash.IndexOf("://", StringComparison.Ordinal);
            var scheme = withoutHash[..schemeEnd].ToLowerInvariant();
            var rest = withoutHash[(schemeEnd + 3)..];

            string host;
            int port;

            if (scheme is "ss")
            {
                // ss://method:pass@host:port or ss://base64
                var at = rest.LastIndexOf('@');
                if (at < 0)
                {
                    name ??= "Shadowsocks";
                    return new ServerProfile
                    {
                        Name = name,
                        Protocol = "ss",
                        RawUri = uri,
                        Host = null,
                        Port = 0
                    };
                }

                var hostPort = rest[(at + 1)..];
                (host, port) = SplitHostPort(hostPort);
            }
            else if (scheme is "hysteria")
            {
                // hysteria://host:port?auth=...  (auth may also be in userinfo)
                var q = rest.IndexOf('?');
                var hostPart = q >= 0 ? rest[..q] : rest;
                var at = hostPart.IndexOf('@');
                if (at >= 0)
                    hostPart = hostPart[(at + 1)..];
                (host, port) = SplitHostPort(hostPart);
            }
            else if (scheme is "wireguard" or "wg")
            {
                var at = rest.LastIndexOf('@');
                var hostPart = at >= 0 ? rest[(at + 1)..] : rest;
                var q = hostPart.IndexOf('?');
                if (q >= 0)
                    hostPart = hostPart[..q];
                (host, port) = SplitHostPort(hostPart);
            }
            else
            {
                // user@host:port?query
                var at = rest.IndexOf('@');
                var hostPart = at >= 0 ? rest[(at + 1)..] : rest;
                var q = hostPart.IndexOf('?');
                if (q >= 0)
                    hostPart = hostPart[..q];
                (host, port) = SplitHostPort(hostPart);
            }

            var proto = scheme switch
            {
                "hy2" => "hysteria2",
                "wg" => "wireguard",
                _ => scheme
            };
            name ??= $"{proto}://{host}:{port}";

            return new ServerProfile
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"{proto} {host}" : name,
                Protocol = proto,
                RawUri = uri,
                Host = host,
                Port = port
            };
        }
        catch
        {
            return null;
        }
    }

    private static ServerProfile? ParseVmess(string uri)
    {
        try
        {
            var b64 = uri["vmess://".Length..];
            var pad = b64.Length % 4;
            if (pad != 0)
                b64 = b64.PadRight(b64.Length + (4 - pad), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var name = ExtractJsonString(json, "ps") ?? ExtractJsonString(json, "remark") ?? "VMess";
            var host = ExtractJsonString(json, "add") ?? ExtractJsonString(json, "host");
            var portStr = ExtractJsonString(json, "port") ?? "0";
            _ = int.TryParse(portStr, out var port);

            return new ServerProfile
            {
                Name = name,
                Protocol = "vmess",
                RawUri = uri,
                Host = host,
                Port = port
            };
        }
        catch
        {
            return new ServerProfile
            {
                Name = "VMess",
                Protocol = "vmess",
                RawUri = uri,
                Host = null,
                Port = 0
            };
        }
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static (string host, int port) SplitHostPort(string hostPort)
    {
        if (hostPort.StartsWith('['))
        {
            var end = hostPort.IndexOf(']');
            var host = hostPort[1..end];
            var portPart = hostPort[(end + 1)..].TrimStart(':');
            return (host, int.TryParse(portPart, out var p) ? p : 0);
        }

        var idx = hostPort.LastIndexOf(':');
        if (idx < 0)
            return (hostPort, 0);
        return (hostPort[..idx], int.TryParse(hostPort[(idx + 1)..], out var port) ? port : 0);
    }
}
