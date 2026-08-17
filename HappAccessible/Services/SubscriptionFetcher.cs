using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HappAccessible.Services;

public sealed class SubscriptionFetcher
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static readonly string[] FallbackAgents =
    [
        // Remnawave (Geodema): URI-list only with HWID + these UAs; Happ gets Xray JSON we don't parse yet
        "HiddifyNext/2.5.7",
        "v2rayN/6.45",
        "nekobox",
        "v2rayNG/1.8.19",
        "Happ/3.3.6"
    ];

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

        if (!IsHttpUrl(input))
            return input;

        EnsureHwid(settings);

        var agents = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CustomUserAgent))
            agents.Add(settings.CustomUserAgent.Trim());
        if (!string.IsNullOrWhiteSpace(settings.LastSuccessfulUserAgent))
            agents.Add(settings.LastSuccessfulUserAgent.Trim());
        agents.AddRange(FallbackAgents);

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
                settings.Save();
                SaveCache(input, body);
                return body;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
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

    private static void SaveCache(string url, string body)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = CachePath(url);
            File.WriteAllText(path, body, Encoding.UTF8);
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
            return File.ReadAllText(path, Encoding.UTF8);
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

        if (s.StartsWith("happ://add/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["happ://add/".Length..];
            try { rest = Uri.UnescapeDataString(rest); } catch { /* ignore */ }
            s = rest.Trim();
        }

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
        req.Headers.TryAddWithoutValidation("x-device-os", "Windows");
        req.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        req.Headers.TryAddWithoutValidation("x-device-model", "PC");
        req.Headers.TryAddWithoutValidation("x-app-version", "3.3.6");
    }

    private static void EnsureHwid(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DeviceHwid)
            && settings.DeviceHwid.Length is >= 10 and <= 64)
            return;

        var bytes = RandomNumberGenerator.GetBytes(12);
        settings.DeviceHwid = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        if (settings.DeviceHwid.Length < 10)
            settings.DeviceHwid = settings.DeviceHwid.PadRight(12, 'A');
        settings.Save();
    }

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
}
