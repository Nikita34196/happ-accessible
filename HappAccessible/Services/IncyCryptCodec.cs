using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappAccessible.Services;

/// <summary>
/// Decodes <c>incy://cryptN/</c> using the published MIT encoder
/// (<see href="https://github.com/INCY-DEV/incy-link-encoder"/>).
/// Built-in crypt1 stays; extra schemes come from <see cref="IncyCompatStore"/> auto-update.
/// </summary>
public static class IncyCryptCodec
{
    public const string Prefix = "incy://crypt1/";
    public const string KeyFingerprint =
        "b6bf708471cc90043232967660aade86a50b4e57929db2e53c5fa34db624c08c";

    private static readonly byte[] KeymatASlice =
        Convert.FromHexString("ee876a063af704d7b409f1910d9731cc1419081ec08993a01270556873e1ef2d");
    private static readonly byte[] KeymatBSlice =
        Convert.FromHexString("cf510df43b2ab97ea98478eee5f1b1cf80e3a484fc9369316ccd87e54b7997b7");

    private static readonly object Gate = new();
    private static List<RuntimeScheme> _schemes = CreateBuiltIn();

    static IncyCryptCodec()
    {
        ReloadFromStore();
    }

    public static void ReloadFromStore()
    {
        var merged = CreateBuiltIn();
        try
        {
            foreach (var s in IncyCompatStore.Load().Schemes)
            {
                if (!TryToRuntime(s, out var runtime))
                    continue;
                merged.RemoveAll(x => string.Equals(x.Host, runtime.Host, StringComparison.OrdinalIgnoreCase));
                merged.Add(runtime);
            }
        }
        catch
        {
            // keep built-in
        }

        lock (Gate)
            _schemes = merged;
    }

    public static bool IsCryptLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return IndexOfCrypt(text) >= 0;
    }

    public static bool IsCrypt1(string? text) => IsCryptLink(text);

    public static bool TryExtract(string text, out string link)
    {
        link = "";
        var i = IndexOfCrypt(text);
        if (i < 0)
            return false;
        var rest = text[i..];
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]) && rest[end] is not '<' and not '"' and not '\'')
            end++;
        link = rest[..end].TrimEnd('/', '>', '.', ',', ';');
        return link.Contains("://crypt", StringComparison.OrdinalIgnoreCase);
    }

    public static IncyCryptPayload Decrypt(string link)
    {
        if (!TryExtract(link, out var extracted))
            throw new InvalidOperationException("Ожидалась ссылка incy://crypt…/");

        List<RuntimeScheme> schemes;
        lock (Gate)
            schemes = [.. _schemes];

        var matching = schemes.Where(s => extracted.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var order = matching.Count > 0 ? matching.Concat(schemes.Except(matching)) : schemes;

        InvalidOperationException? last = null;
        foreach (var scheme in order)
        {
            if (!extracted.StartsWith(scheme.Prefix, StringComparison.OrdinalIgnoreCase)
                && matching.Count > 0)
                continue;
            try
            {
                return DecryptWith(scheme, extracted);
            }
            catch (InvalidOperationException ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException(
            "Не удалось расшифровать incy://crypt…/ (ссылка повреждена или нужна новая схема — Справка → Обновить совместимость INCY).");
    }

    public static bool TryDecrypt(string text, out IncyCryptPayload payload)
    {
        payload = default;
        try
        {
            payload = Decrypt(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IncyCryptPayload DecryptWith(RuntimeScheme scheme, string extracted)
    {
        var payload = extracted[scheme.Prefix.Length..].TrimEnd('/');
        if (payload.Length == 0)
            throw new InvalidOperationException("Пустой payload " + scheme.Prefix);

        byte[] wire;
        try
        {
            wire = DecodeBase64Url(payload);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Некорректный base64url в " + scheme.Prefix);
        }

        const int ivLen = 12;
        const int tagLen = 16;
        if (wire.Length < ivLen + tagLen + 1)
            throw new InvalidOperationException("Слишком короткий payload " + scheme.Prefix);

        var plaintext = new byte[wire.Length - ivLen - tagLen];
        try
        {
            using var aes = new AesGcm(scheme.Key, tagLen);
            aes.Decrypt(wire.AsSpan(0, ivLen), wire.AsSpan(ivLen, plaintext.Length), wire.AsSpan(wire.Length - tagLen, tagLen), plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Ключ " + scheme.Host + " не подошёл.");
        }

        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(plaintext);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(scheme.Prefix + " расшифровался, но внутри не JSON.");
        }

        if (!parsed.TryGetProperty("url", out var urlEl) || urlEl.GetString() is not { Length: > 0 } url)
            throw new InvalidOperationException("В " + scheme.Prefix + " нет поля url.");

        string? name = null;
        if (parsed.TryGetProperty("n", out var nameEl) && nameEl.GetString() is { Length: > 0 } n)
            name = n;

        return new IncyCryptPayload(url.Trim(), name);
    }

    private static int IndexOfCrypt(string text)
    {
        var i = text.IndexOf("incy://crypt", StringComparison.OrdinalIgnoreCase);
        return i;
    }

    private static List<RuntimeScheme> CreateBuiltIn()
    {
        var salt = Encoding.UTF8.GetBytes("incy" + "deep" + "crypt1" + "v2026.06");
        var seed = new byte[salt.Length + KeymatASlice.Length + KeymatBSlice.Length];
        Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
        Buffer.BlockCopy(KeymatASlice, 0, seed, salt.Length, KeymatASlice.Length);
        Buffer.BlockCopy(KeymatBSlice, 0, seed, salt.Length + KeymatASlice.Length, KeymatBSlice.Length);
        var key = SHA256.HashData(seed);
        var fp = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        if (!string.Equals(fp, KeyFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Отпечаток встроенного ключа incy crypt1 не совпал.");

#if DEBUG
        VerifyPinnedVector(key);
#endif
        return
        [
            new RuntimeScheme("crypt1", Prefix, key)
        ];
    }

    private static bool TryToRuntime(IncyCryptScheme s, out RuntimeScheme runtime)
    {
        runtime = null!;
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.KeyHex))
            return false;
        try
        {
            var key = Convert.FromHexString(s.KeyHex.Trim());
            if (key.Length != 32)
                return false;
            if (!string.IsNullOrWhiteSpace(s.Fingerprint))
            {
                var fp = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
                if (!string.Equals(fp, s.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var prefix = string.IsNullOrWhiteSpace(s.Prefix) ? "incy://" + s.Host + "/" : s.Prefix;
            if (!prefix.EndsWith('/'))
                prefix += "/";
            runtime = new RuntimeScheme(s.Host.Trim(), prefix, key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyPinnedVector(byte[] key)
    {
        const string pinned =
            "incy://crypt1/AAECAwQFBgcICQoLNyIQL3rDwRZqnyoD8pGKSLXP6o8NdSXQVSSALNbbUyIr__tWGFUexdIfKvvmDnuDGbmBvuppfNef6aKNZUwOm4c-Sg";
        var payload = pinned[Prefix.Length..];
        var wire = DecodeBase64Url(payload);
        var plaintext = new byte[wire.Length - 12 - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(wire.AsSpan(0, 12), wire.AsSpan(12, plaintext.Length), wire.AsSpan(wire.Length - 16, 16), plaintext);
        var json = Encoding.UTF8.GetString(plaintext);
        if (!json.Contains("https://sub.example.com/test-vector", StringComparison.Ordinal))
            throw new InvalidOperationException("Тестовый вектор incy crypt1 не расшифровался.");
    }

    internal static byte[] DecodeBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        var pad = (4 - padded.Length % 4) % 4;
        if (pad > 0)
            padded = padded.PadRight(padded.Length + pad, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record RuntimeScheme(string Host, string Prefix, byte[] Key);
}

public readonly record struct IncyCryptPayload(string Url, string? Name);
