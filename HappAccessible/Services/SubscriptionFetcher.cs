using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class SubscriptionFetcher
{
    // Compatibility identity used by panels that recognize the official Happ client.
    private const string CompatibilityUserAgent = "Happ/3.3.6";
    // INCY panels expect this family of User-Agent / x-client values.
    // Bump when desktop INCY releases a version that panels start requiring
    // (INCY-DEV/incy-platforms releases + docs.incy.cc subscription-format).
    private const string IncyCompatibilityUserAgent = "INCY/3.7.2";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "cache");

    public async Task<string> FetchAsync(
        string urlOrContent,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var input = NormalizeInput(urlOrContent);
        if (CryptLinkHandler.IsHappCrypt(input))
            throw new InvalidOperationException(CryptLinkHandler.ExplainLimitation());
        if (IncyCryptCodec.IsCrypt1(input))
            throw new InvalidOperationException("Не удалось расшифровать incy://crypt1/.");

        if (!IsHttpUrl(input))
            return input;

        EnsureHwid(settings);

        var agents = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CustomUserAgent))
            agents.Add(settings.CustomUserAgent.Trim());
        if (settings.LastSuccessfulUserAgent is { } lastUa && IsOwnUserAgent(lastUa))
            agents.Add(lastUa.Trim());
        agents.Add(GetApplicationUserAgent());
        agents.Add(IncyCompatibilityUserAgent);

        Exception? lastError = null;
        HttpStatusCode? lastCode = null;
        string? lastSnippet = null;

        // One URL only — do not spam trailing-slash / header variants
        foreach (var ua in agents.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, input);
                ApplyHeaders(req, settings, ua);

                using var response = await Http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                lastCode = response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    lastSnippet = Truncate(StripHtml(body), 100);
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    // brief pause before next UA to avoid rate-limit
                    await Task.Delay(400, ct).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(body) || LooksLikeHtmlError(body))
                {
                    lastError = new InvalidOperationException("Пустой или HTML-ответ.");
                    continue;
                }

                if (LooksLikeAppNotSupportedStub(body))
                {
                    lastError = new InvalidOperationException(
                        "Панель вернула «App not supported» — нужен HWID устройства (уже отправляем) или другой клиентский User-Agent.");
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    continue;
                }

                var score = SubscriptionParser.Parse(body).Count;
                if (score <= 0)
                {
                    lastError = new InvalidOperationException("Ответ без распознанных серверов.");
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    continue;
                }

                settings.LastSuccessfulUserAgent = ua;
                ApplyUserInfo(settings, response);
                settings.SubscriptionLastUpdateUtc = DateTimeOffset.UtcNow;
                settings.Save();
                SaveCache(input, body);
                return body;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLogService.Error($"Subscription fetch failed ({SafeHost(input)}, UA={ua})", ex);
                if (IsTransportFailure(ex))
                    break;
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }

        // Fallback: recent cache (6 hours)
        var cached = TryLoadCache(input, TimeSpan.FromHours(6));
        if (cached is not null && SubscriptionParser.Parse(cached).Count > 0)
            return cached;

        var code = lastCode is null ? "?" : ((int)lastCode).ToString();
        var hint = code is "429" or "503" or "502" or "500"
            ? " Сервер временно отклоняет запросы — подождите 1–2 минуты и нажмите Загрузить снова."
            : code is "404"
                ? " Ссылка не найдена — перевыпустите в боте."
                : " Если только что загружалось успешно — чаще всего лимит запросов; подождите и повторите.";

        throw new InvalidOperationException(
            $"Не удалось загрузить подписку (HTTP {code}, {SafeHost(input)}).{hint}"
            + DescribeFetchFailure(lastError)
            + (lastSnippet is null ? "" : " Ответ: " + lastSnippet)
            + (lastError is null ? "" : " (" + lastError.Message + ")"));
    }

    public static string? TryLoadCacheOnly(string urlOrContent)
    {
        var input = NormalizeInput(urlOrContent);
        if (!IsHttpUrl(input))
            return null;
        return TryLoadCache(input, TimeSpan.FromHours(24));
    }

    public static DateTimeOffset? TryGetCacheTimestamp(string urlOrContent)
    {
        var input = NormalizeInput(urlOrContent);
        if (!IsHttpUrl(input))
            return null;
        try
        {
            var meta = CachePath(input) + ".meta";
            if (!File.Exists(meta))
                return null;
            return DateTimeOffset.TryParse(File.ReadAllText(meta), out var when) ? when : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyUserInfo(AppSettings settings, HttpResponseMessage response)
    {
        // Metadata belongs to this response. Do not keep an expiry or quota
        // from a different subscription when the provider omits the header.
        settings.SubscriptionUploadBytes = null;
        settings.SubscriptionDownloadBytes = null;
        settings.SubscriptionTotalBytes = null;
        settings.SubscriptionExpireUnix = null;
        settings.SubscriptionProfileTitle = null;
        settings.SubscriptionSupportUrl = null;
        settings.SubscriptionProfileUpdateIntervalHours = null;
        settings.PendingRoutingLink = null;

        string? raw = null;
        if (response.Headers.TryGetValues("subscription-userinfo", out var a))
            raw = a.FirstOrDefault();
        else if (response.Headers.TryGetValues("Subscription-Userinfo", out var b))
            raw = b.FirstOrDefault();
        else if (response.Content.Headers.TryGetValues("subscription-userinfo", out var c))
            raw = c.FirstOrDefault();

        var info = SubscriptionUserInfo.Parse(raw);
        if (info is not null)
        {
            settings.SubscriptionUploadBytes = info.UploadBytes;
            settings.SubscriptionDownloadBytes = info.DownloadBytes;
            settings.SubscriptionTotalBytes = info.TotalBytes;
            settings.SubscriptionExpireUnix = info.ExpireUnix > 0 ? info.ExpireUnix : null;
        }

        var title = GetHeader(response, "profile-title");
        if (!string.IsNullOrWhiteSpace(title))
            settings.SubscriptionProfileTitle = DecodeHeaderValue(title);

        var supportUrl = GetHeader(response, "support-url");
        if (!string.IsNullOrWhiteSpace(supportUrl))
            settings.SubscriptionSupportUrl = supportUrl.Trim();

        var interval = GetHeader(response, "profile-update-interval");
        if (int.TryParse(interval, out var hours) && hours > 0)
            settings.SubscriptionProfileUpdateIntervalHours = Math.Min(hours, 168);

        var routing = GetHeader(response, "routing") ?? GetHeader(response, "autorouting");
        if (!string.IsNullOrWhiteSpace(routing) && !routing.Trim().Equals("0", StringComparison.Ordinal))
            settings.PendingRoutingLink = routing.Trim();
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out var contentValues))
            return contentValues.FirstOrDefault();
        return null;
    }

    private static string DecodeHeaderValue(string value)
    {
        var text = value.Trim();
        try
        {
            var encoded = text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
                ? text["base64:".Length..]
                : text;
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim();
            if (decoded.Any(char.IsLetterOrDigit))
                return decoded;
        }
        catch
        {
            // Some panels return a plain UTF-8 title.
        }
        return text;
    }

    private static void SaveCache(string url, string body)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = CachePath(url);
            var protectedBody = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(body),
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBody);
            File.WriteAllText(path + ".meta", DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
        }
        catch
        {
            // ignore cache errors
        }
    }

    private static string? TryLoadCache(string url, TimeSpan maxAge)
    {
        try
        {
            var path = CachePath(url);
            var meta = path + ".meta";
            if (!File.Exists(path) || !File.Exists(meta))
                return null;
            if (!DateTimeOffset.TryParse(File.ReadAllText(meta), out var when))
                return null;
            if (DateTimeOffset.UtcNow - when > maxAge)
                return null;
            try
            {
                var protectedBody = File.ReadAllBytes(path);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    protectedBody,
                    null,
                    DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                // Migrate a cache created by versions before DPAPI protection.
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string CachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(CacheDir, hash + ".sub");
    }

    public static string NormalizeInput(string raw)
    {
        var s = (raw ?? "").Trim().Trim('"').Trim('\'');
        s = s.Replace("\r", "").Replace("\n", "").Trim();

        if (s.StartsWith('<') && s.EndsWith('>') && s.Length > 2)
            s = s[1..^1].Trim();

        if (s.StartsWith("happ://add/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["happ://add/".Length..];
            try { rest = Uri.UnescapeDataString(rest); } catch { /* ignore */ }
            s = rest.Trim();
        }

        if (IncyCryptCodec.IsCrypt1(s) && IncyCryptCodec.TryDecrypt(s, out var crypt))
            s = crypt.Url.Trim();
        else if (s.StartsWith("incy://add/", StringComparison.OrdinalIgnoreCase)
                 || s.StartsWith("incy://import/", StringComparison.OrdinalIgnoreCase))
            s = IncyDeepLink.UnwrapSubscriptionInput(s).Trim();

        if (s.StartsWith("sub://", StringComparison.OrdinalIgnoreCase))
        {
            var b64 = s["sub://".Length..];
            var hash = b64.IndexOf('#');
            if (hash >= 0) b64 = b64[..hash];
            try
            {
                var pad = b64.Length % 4;
                if (pad != 0) b64 = b64.PadRight(b64.Length + (4 - pad), '=');
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                if (IsHttpUrl(decoded)) s = decoded.Trim();
            }
            catch { /* ignore */ }
        }

        return s;
    }

    private static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static void ApplyHeaders(HttpRequestMessage req, AppSettings settings, string userAgent)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("x-hwid", settings.DeviceHwid!);
        req.Headers.TryAddWithoutValidation("X-Device-ID", settings.DeviceHwid!);
        req.Headers.TryAddWithoutValidation("x-device-os", "Windows");
        req.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        req.Headers.TryAddWithoutValidation("x-device-model", "PC");
        if (userAgent.StartsWith("INCY/", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("x-client", "INCY");
            req.Headers.TryAddWithoutValidation("x-app-version", userAgent["INCY/".Length..].Trim());
        }
        else
            req.Headers.TryAddWithoutValidation("x-app-version", "3.3.6");
    }

    private static string GetApplicationUserAgent() =>
        CompatibilityUserAgent;

    private static bool IsOwnUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return false;
        var ua = userAgent.Trim();
        if (ua.StartsWith("HappAccessible/", StringComparison.OrdinalIgnoreCase))
            return false;
        return ua.StartsWith("Happ/", StringComparison.OrdinalIgnoreCase)
               || ua.StartsWith("INCY/", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureHwid(AppSettings settings) =>
        DeviceHwidService.EnsureHwid(settings);

    private static bool LooksLikeAppNotSupportedStub(string body)
    {
        var t = body.Trim();
        if (t.Contains("App not supported", StringComparison.OrdinalIgnoreCase))
            return true;
        // Typical Remnawave stub: short base64 of vless://…@0.0.0.0:1#App%20not%20supported
        if (t.Length is > 40 and < 400 && !t.Contains('{') && !t.Contains("proxies:"))
        {
            try
            {
                var cleaned = Regex.Replace(t, @"\s+", "");
                var pad = cleaned.Length % 4;
                if (pad != 0)
                    cleaned = cleaned.PadRight(cleaned.Length + (4 - pad), '=');
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
                if (decoded.Contains("0.0.0.0:1", StringComparison.Ordinal)
                    || decoded.Contains("App%20not%20supported", StringComparison.OrdinalIgnoreCase)
                    || decoded.Contains("App not supported", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // not base64
            }
        }

        return false;
    }

    private static bool LooksLikeHtmlError(string body)
    {
        var t = body.TrimStart();
        if (!t.StartsWith('<')) return false;
        return Regex.IsMatch(t, "404|not found|error", RegexOptions.IgnoreCase);
    }

    private static string StripHtml(string s) => Regex.Replace(s, "<[^>]+>", " ");

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return "?"; }
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static bool IsTransportFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException or AuthenticationException or IOException or SocketException)
                return true;

            var msg = cur.Message;
            if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string DescribeFetchFailure(Exception? ex)
    {
        if (ex is null || !IsTransportFailure(ex))
            return "";

        return " Проверьте интернет, отключите VPN и системный прокси Windows, затем нажмите «Обновить подписку».";
    }
}
