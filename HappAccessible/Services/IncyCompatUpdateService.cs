using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HappAccessible.Services;

/// <summary>
/// Pulls INCY desktop User-Agent and published crypt key material from GitHub.
/// Does not download INCY binaries — only public encoder assets and release tags.
/// </summary>
public sealed class IncyCompatUpdateService
{
    public const string FallbackUserAgent = "INCY/3.7.2";

    private static readonly HttpClient Http =
        DirectHttp.Create(TimeSpan.FromSeconds(45), "HappAccessible/" + AppUpdateService.GetCurrentVersion());

    public async Task<string> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var state = IncyCompatStore.Load();
        if (!force
            && state.CheckedUtc is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(12))
            return "";

        var notes = new List<string>();

        try
        {
            var desktop = await FetchLatestDesktopVersionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                var ua = "INCY/" + desktop;
                if (!string.Equals(state.UserAgent, ua, StringComparison.OrdinalIgnoreCase))
                {
                    state.UserAgent = ua;
                    state.DesktopVersion = desktop;
                    notes.Add("User-Agent " + ua);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error("INCY desktop version check failed", ex);
        }

        try
        {
            var added = await RefreshCryptSchemesAsync(state, ct).ConfigureAwait(false);
            notes.AddRange(added);
        }
        catch (Exception ex)
        {
            AppLogService.Error("INCY crypt scheme refresh failed", ex);
        }

        state.CheckedUtc = DateTimeOffset.UtcNow;
        IncyCompatStore.Save(state);
        IncyCryptCodec.ReloadFromStore();
        return string.Join("; ", notes);
    }

    private static async Task<string?> FetchLatestDesktopVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/INCY-DEV/incy-platforms/releases?per_page=15");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var m = Regex.Match(tag, @"desktop-v(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value;
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> RefreshCryptSchemesAsync(
        IncyCompatState state,
        CancellationToken ct)
    {
        var notes = new List<string>();
        var core = await Http.GetStringAsync(
            "https://raw.githubusercontent.com/INCY-DEV/incy-link-encoder/main/src/core.ts", ct)
            .ConfigureAwait(false);

        var fingerprint = ReadStringConst(core, "EXPECTED_KEY_FINGERPRINT");
        var host = ReadStringConst(core, "HOST") ?? "crypt1";
        var salt = string.Concat(
            ReadStringConst(core, "SALT_P1") ?? "incy",
            ReadStringConst(core, "SALT_P2") ?? "deep",
            ReadStringConst(core, "SALT_P3") ?? "crypt1",
            ReadStringConst(core, "SALT_P4") ?? "v2026.06");
        var offsetA = ReadIntConst(core, "KEYMAT_A_OFFSET") ?? 1024;
        var offsetB = ReadIntConst(core, "KEYMAT_B_OFFSET") ?? 2048;
        var keyLen = ReadIntConst(core, "KEYMAT_LEN") ?? 32;

        if (string.IsNullOrWhiteSpace(fingerprint))
            return notes;

        byte[]? assetsA = null;
        byte[]? assetsB = null;

        async Task<(byte[] A, byte[] B)> AssetsAsync()
        {
            assetsA ??= await Http.GetByteArrayAsync(
                "https://raw.githubusercontent.com/INCY-DEV/incy-link-encoder/main/assets/incy_assets_a.bin", ct)
                .ConfigureAwait(false);
            assetsB ??= await Http.GetByteArrayAsync(
                "https://raw.githubusercontent.com/INCY-DEV/incy-link-encoder/main/assets/incy_assets_b.bin", ct)
                .ConfigureAwait(false);
            return (assetsA, assetsB);
        }

        var prefix = "incy://" + host + "/";
        if (!HasScheme(state, host, fingerprint))
        {
            var (a, b) = await AssetsAsync().ConfigureAwait(false);
            var scheme = DeriveScheme(host, prefix, salt, fingerprint, a, b, offsetA, offsetB, keyLen);
            Upsert(state, scheme);
            notes.Add("ключ " + host);
        }

        foreach (var extra in ParseSchemesBlock(core))
        {
            if (HasScheme(state, extra.Host, extra.Fingerprint))
                continue;
            if (string.IsNullOrWhiteSpace(extra.Fingerprint) || extra.Fingerprint.Length != 64)
                continue;

            var extraSalt = salt.Contains(extra.Host, StringComparison.OrdinalIgnoreCase)
                ? salt
                : GuessSaltForHost(core, extra.Host, salt);
            var (a, b) = await AssetsAsync().ConfigureAwait(false);
            try
            {
                var scheme = DeriveScheme(
                    extra.Host, extra.Prefix, extraSalt, extra.Fingerprint,
                    a, b, offsetA, offsetB, keyLen);
                Upsert(state, scheme);
                notes.Add("схема " + extra.Host);
            }
            catch (Exception ex)
            {
                AppLogService.Error("INCY scheme " + extra.Host + " derive failed", ex);
            }
        }

        return notes;
    }

    private static IncyCryptScheme DeriveScheme(
        string host,
        string prefix,
        string salt,
        string fingerprint,
        byte[] assetA,
        byte[] assetB,
        int offsetA,
        int offsetB,
        int keyLen)
    {
        if (assetA.Length < offsetA + keyLen || assetB.Length < offsetB + keyLen)
            throw new InvalidOperationException("keymat INCY короче, чем ожидалось.");

        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var seed = new byte[saltBytes.Length + keyLen * 2];
        Buffer.BlockCopy(saltBytes, 0, seed, 0, saltBytes.Length);
        Buffer.BlockCopy(assetA, offsetA, seed, saltBytes.Length, keyLen);
        Buffer.BlockCopy(assetB, offsetB, seed, saltBytes.Length + keyLen, keyLen);
        var key = SHA256.HashData(seed);
        var fp = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        if (!string.Equals(fp, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Отпечаток ключа INCY не совпал (" + host + ").");

        return new IncyCryptScheme
        {
            Host = host,
            Prefix = prefix.EndsWith('/') ? prefix : prefix + "/",
            Fingerprint = fingerprint.ToLowerInvariant(),
            Salt = salt,
            KeymatAOffset = offsetA,
            KeymatBOffset = offsetB,
            KeyHex = Convert.ToHexString(key).ToLowerInvariant()
        };
    }

    private static bool HasScheme(IncyCompatState state, string host, string fingerprint) =>
        state.Schemes.Any(s =>
            string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(s.KeyHex));

    private static void Upsert(IncyCompatState state, IncyCryptScheme scheme)
    {
        state.Schemes.RemoveAll(s => string.Equals(s.Host, scheme.Host, StringComparison.OrdinalIgnoreCase));
        state.Schemes.Add(scheme);
    }

    private static string? ReadStringConst(string src, string name)
    {
        var m = Regex.Match(
            src,
            @"(?:export\s+)?const\s+" + Regex.Escape(name) + @"\s*=\s*['""]([^'""]+)['""]",
            RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int? ReadIntConst(string src, string name)
    {
        var m = Regex.Match(
            src,
            @"(?:export\s+)?const\s+" + Regex.Escape(name) + @"\s*=\s*(\d+)",
            RegexOptions.Multiline);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static List<(string Host, string Prefix, string Fingerprint)> ParseSchemesBlock(string src)
    {
        var list = new List<(string, string, string)>();
        var block = Regex.Match(src, @"SCHEMES\s*[:=][\s\S]*?\{([\s\S]*?)\n\}\s*\)", RegexOptions.Multiline);
        var body = block.Success ? block.Groups[1].Value : src;
        foreach (Match m in Regex.Matches(
                     body,
                     @"crypt\d+\s*:\s*(?:Object\.freeze\()?\{([^}]+)\}",
                     RegexOptions.IgnoreCase))
        {
            var inner = m.Groups[1].Value;
            var host = Regex.Match(inner, @"host\s*:\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
            var prefix = Regex.Match(inner, @"prefix\s*:\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
            var fpLit = Regex.Match(inner, @"keyFingerprint\s*:\s*['""]([0-9a-fA-F]{64})['""]", RegexOptions.IgnoreCase);
            var fpName = Regex.Match(inner, @"keyFingerprint\s*:\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            var fingerprint = fpLit.Success
                ? fpLit.Groups[1].Value
                : fpName.Success ? ReadStringConst(src, fpName.Groups[1].Value) ?? "" : "";
            var h = host.Success ? host.Groups[1].Value : "";
            if (h.Length == 0)
                continue;
            var p = prefix.Success ? prefix.Groups[1].Value : "incy://" + h + "/";
            list.Add((h, p, fingerprint.ToLowerInvariant()));
        }

        return list;
    }

    private static string GuessSaltForHost(string src, string host, string fallback)
    {
        var m = Regex.Match(
            src,
            @"SALT_P1[^=]*=\s*['""]([^'""]+)['""][\s\S]{0,200}?SALT_P2[^=]*=\s*['""]([^'""]+)['""][\s\S]{0,200}?SALT_P3[^=]*=\s*['""]("
            + Regex.Escape(host) + @")['""][\s\S]{0,200}?SALT_P4[^=]*=\s*['""]([^'""]+)['""]",
            RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
        return fallback.Replace("crypt1", host, StringComparison.OrdinalIgnoreCase);
    }
}
