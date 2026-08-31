using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace HappAccessible.Services;

public enum IncyLinkKind
{
    None,
    Crypt1,
    Subscription,
    RoutingProfile,
    RoutingOff,
    VpnControl
}

public sealed class IncyParsedLink
{
    public IncyLinkKind Kind { get; init; }
    public string Raw { get; init; } = "";
    public string Payload { get; init; } = "";
    public string? ProviderName { get; init; }
    public bool ActivateRouting { get; init; }
    public string? ControlAction { get; init; }
}

public static class IncyDeepLink
{
    private static readonly Regex IncyUrlRx = new(
        @"incy://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoutingLineRx = new(
        @"^(?:incy:)?://(?:autorouting|routing|onadd)/[^\s]+$|^incy://(?:autorouting|routing|onadd)/[^\s]+$|^happ://routing/[^\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProfileTitleRx = new(
        @"^#profile-title:\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string text, out IncyParsedLink link)
    {
        link = new IncyParsedLink();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var raw = ExtractFirst(text.Trim());
        if (string.IsNullOrEmpty(raw))
            return false;

        if (raw.StartsWith("happ://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TrySplit(raw, out var host, out var path, out var query))
            return false;

        host = host.ToLowerInvariant();
        path = path.Trim('/');

        if (host is "crypt1")
        {
            if (!IncyCryptCodec.TryDecrypt(raw, out var decrypted))
                throw new InvalidOperationException("Не удалось расшифровать incy://crypt1/.");
            link = new IncyParsedLink
            {
                Kind = IncyLinkKind.Crypt1,
                Raw = raw,
                Payload = decrypted.Url,
                ProviderName = decrypted.Name
            };
            return true;
        }

        if (host is "connect" or "open" or "disconnect" or "close" or "toggle" or "status")
        {
            link = new IncyParsedLink
            {
                Kind = IncyLinkKind.VpnControl,
                Raw = raw,
                ControlAction = host
            };
            return true;
        }

        if (host is "routing" && path.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            link = new IncyParsedLink { Kind = IncyLinkKind.RoutingOff, Raw = raw };
            return true;
        }

        if (host is "routing" or "autorouting" or "onadd")
        {
            var (payload, activate) = SplitRoutingPath(host, path, query);
            payload = DecodePayload(payload);
            if (string.IsNullOrWhiteSpace(payload))
                throw new InvalidOperationException("В ссылке маршрутизации INCY нет данных.");

            link = new IncyParsedLink
            {
                Kind = IncyLinkKind.RoutingProfile,
                Raw = raw,
                Payload = payload,
                ActivateRouting = activate
            };
            return true;
        }

        if (host is "add" or "import")
        {
            var payload = path.Length > 0 ? path : ReadQueryData(query);
            payload = DecodePayload(payload);
            if (string.IsNullOrWhiteSpace(payload))
                throw new InvalidOperationException("В ссылке incy://add/ или import/ нет данных.");

            link = new IncyParsedLink
            {
                Kind = IncyLinkKind.Subscription,
                Raw = raw,
                Payload = payload
            };
            return true;
        }

        return false;
    }

    private static (string Payload, bool Activate) SplitRoutingPath(string host, string path, string query)
    {
        var activate = host is "autorouting" or "onadd";
        var payload = path;
        if (host is "onadd")
            return (payload.Length > 0 ? payload : ReadQueryData(query), true);

        if (payload.StartsWith("onadd/", StringComparison.OrdinalIgnoreCase))
        {
            activate = true;
            payload = payload["onadd/".Length..];
        }
        else if (payload.Equals("onadd", StringComparison.OrdinalIgnoreCase))
        {
            activate = true;
            payload = "";
        }
        else if (payload.StartsWith("add/", StringComparison.OrdinalIgnoreCase))
            payload = payload["add/".Length..];
        else if (payload.Equals("add", StringComparison.OrdinalIgnoreCase))
            payload = "";

        if (string.IsNullOrEmpty(payload))
            payload = ReadQueryData(query);
        return (payload, activate);
    }

    public static string UnwrapSubscriptionInput(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"').Trim('\'');
        if (s.StartsWith('<') && s.EndsWith('>'))
            s = s[1..^1].Trim();

        if (IncyCryptCodec.IsCrypt1(s) && IncyCryptCodec.TryDecrypt(s, out var crypt))
            return crypt.Url;

        if (TryParse(s, out var parsed) && parsed.Kind is IncyLinkKind.Crypt1 or IncyLinkKind.Subscription)
            return parsed.Payload;

        return s;
    }

    public static (string Body, IReadOnlyList<string> RoutingLinks, string? ProfileTitle)
        SplitSubscriptionBody(string body)
    {
        var routing = new List<string>();
        string? title = null;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                kept.Add(rawLine);
                continue;
            }

            var titleMatch = ProfileTitleRx.Match(line);
            if (titleMatch.Success)
            {
                title ??= DecodeMetaValue(titleMatch.Groups[1].Value.Trim());
                continue;
            }

            if (RoutingLineRx.IsMatch(line)
                || line.StartsWith("incy://routing/", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("incy://autorouting/", StringComparison.OrdinalIgnoreCase)
                || line.Equals("incy://routing/off", StringComparison.OrdinalIgnoreCase)
                || line.Equals("://routing/off", StringComparison.OrdinalIgnoreCase))
            {
                routing.Add(line);
                continue;
            }

            kept.Add(rawLine);
        }

        return (string.Join('\n', kept), routing, title);
    }

    public static async Task<string> ResolveRoutingPayloadAsync(string payload, CancellationToken ct = default)
    {
        payload = payload.Trim();
        if (payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = DirectHttp.Create(
                TimeSpan.FromSeconds(30),
                "HappAccessible/" + AppUpdateService.GetCurrentVersion());
            var json = await http.GetStringAsync(payload, ct).ConfigureAwait(false);
            json = json.Trim();
            if (json.StartsWith('<'))
                throw new InvalidOperationException("URL профиля маршрутизации вернул HTML, а не JSON.");
            return json;
        }

        return payload;
    }

    public static string DescribeVpnControl(string? action) =>
        action switch
        {
            "connect" or "open" =>
                "incy://connect в этом клиенте не запускает туннель. Выберите сервер и нажмите «Подключить» (Ctrl+Shift+C).",
            "disconnect" or "close" =>
                "incy://disconnect здесь не обрабатывается. Нажмите «Отключить» (Ctrl+Shift+D).",
            "toggle" =>
                "incy://toggle здесь не обрабатывается. Используйте «Подключить» / «Отключить».",
            "status" =>
                "incy://status только открывает INCY. Статус смотрите в этом окне.",
            _ => "Эта ссылка управления INCY в доступном клиенте не используется."
        };

    private static string ExtractFirst(string text)
    {
        var m = IncyUrlRx.Match(text);
        if (m.Success)
            return m.Value.TrimEnd('/', '>', '.', ',', ';');

        var trimmed = text.Replace("\r", "").Replace("\n", "").Trim();
        if (trimmed.StartsWith("://", StringComparison.Ordinal))
            return "incy" + trimmed.Split(' ', '\t')[0];

        return "";
    }

    private static bool TrySplit(string raw, out string host, out string path, out string query)
    {
        host = "";
        path = "";
        query = "";
        const string prefix = "incy://";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = raw[prefix.Length..];
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            host = rest;
            return host.Length > 0;
        }

        host = rest[..slash];
        path = rest[slash..];
        return host.Length > 0;
    }

    private static string ReadQueryData(string query)
    {
        if (string.IsNullOrEmpty(query))
            return "";
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq];
            if (key.Equals("data", StringComparison.OrdinalIgnoreCase)
                || key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                try { return Uri.UnescapeDataString(part[(eq + 1)..]); }
                catch { return part[(eq + 1)..]; }
            }
        }

        return "";
    }

    private static string DecodePayload(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0)
            return payload;
        try { payload = Uri.UnescapeDataString(payload); }
        catch { /* keep */ }

        if (LooksLikeDirectPayload(payload))
            return payload.Trim();

        try
        {
            var decoded = Encoding.UTF8.GetString(IncyCryptCodec.DecodeBase64Url(payload)).Trim();
            if (LooksLikeDirectPayload(decoded) || decoded.StartsWith('{'))
                return decoded;
        }
        catch
        {
            // not base64
        }

        return payload.Trim();
    }

    private static bool LooksLikeDirectPayload(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("hysteria", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("wg://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith('{')
        || s.Contains("[Interface]", StringComparison.OrdinalIgnoreCase);

    private static string DecodeMetaValue(string value)
    {
        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b64 = value["base64:".Length..].Trim();
                var decoded = Encoding.UTF8.GetString(IncyCryptCodec.DecodeBase64Url(b64)).Trim();
                var nl = decoded.IndexOfAny(['\r', '\n']);
                return nl >= 0 ? decoded[..nl].Trim() : decoded;
            }
            catch
            {
                return value;
            }
        }

        return value;
    }
}
