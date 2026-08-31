using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappAccessible.Services;

/// <summary>
/// Decodes <c>incy://crypt1/</c> using the published MIT encoder
/// (<see href="https://github.com/INCY-DEV/incy-link-encoder"/>).
/// This is obfuscation, not secrecy: the same KDF ships in every INCY client.
/// </summary>
public static class IncyCryptCodec
{
    public const string Prefix = "incy://crypt1/";
    // SHA-256 of K1 from @incy/link-encoder (crypt1). When they add crypt2, keep this
    // scheme and add a second key — old chat links must keep decoding.
    public const string KeyFingerprint =
        "b6bf708471cc90043232967660aade86a50b4e57929db2e53c5fa34db624c08c";

    // 32-byte slices from official assets/incy_assets_{a,b}.bin (offsets 1024 and 2048).
    // Refresh from https://github.com/INCY-DEV/incy-link-encoder if the fingerprint drifts.
    private static readonly byte[] KeymatASlice =
        Convert.FromHexString("ee876a063af704d7b409f1910d9731cc1419081ec08993a01270556873e1ef2d");
    private static readonly byte[] KeymatBSlice =
        Convert.FromHexString("cf510df43b2ab97ea98478eee5f1b1cf80e3a484fc9369316ccd87e54b7997b7");

    private static readonly byte[] AesKey = DeriveKey();

    public static bool IsCrypt1(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return IndexOfCrypt1(text) >= 0;
    }

    public static bool TryExtract(string text, out string link)
    {
        link = "";
        var i = IndexOfCrypt1(text);
        if (i < 0)
            return false;
        var rest = text[i..];
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]) && rest[end] is not '<' and not '"' and not '\'')
            end++;
        link = rest[..end].TrimEnd('/', '>', '.', ',', ';');
        return link.Length > Prefix.Length;
    }

    public static IncyCryptPayload Decrypt(string link)
    {
        if (!TryExtract(link, out var extracted))
            throw new InvalidOperationException("Ожидалась ссылка incy://crypt1/…");

        var payload = extracted[Prefix.Length..].TrimEnd('/');
        if (payload.Length == 0)
            throw new InvalidOperationException("Пустой payload incy://crypt1/.");

        byte[] wire;
        try
        {
            wire = DecodeBase64Url(payload);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Некорректный base64url в incy://crypt1/.");
        }

        const int ivLen = 12;
        const int tagLen = 16;
        if (wire.Length < ivLen + tagLen + 1)
            throw new InvalidOperationException("Слишком короткий payload incy://crypt1/.");

        var nonce = wire.AsSpan(0, ivLen);
        var tag = wire.AsSpan(wire.Length - tagLen, tagLen);
        var ciphertext = wire.AsSpan(ivLen, wire.Length - ivLen - tagLen);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(AesKey, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                "Не удалось расшифровать incy://crypt1/ (ссылка повреждена или это уже crypt2).");
        }

        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(plaintext);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("incy://crypt1/ расшифровался, но внутри не JSON.");
        }

        if (!parsed.TryGetProperty("url", out var urlEl) || urlEl.GetString() is not { Length: > 0 } url)
            throw new InvalidOperationException("В incy://crypt1/ нет поля url.");

        string? name = null;
        if (parsed.TryGetProperty("n", out var nameEl) && nameEl.GetString() is { Length: > 0 } n)
            name = n;

        return new IncyCryptPayload(url.Trim(), name);
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

    private static int IndexOfCrypt1(string text) =>
        text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);

    private static byte[] DeriveKey()
    {
        var salt = Encoding.UTF8.GetBytes("incy" + "deep" + "crypt1" + "v2026.06");
        var seed = new byte[salt.Length + KeymatASlice.Length + KeymatBSlice.Length];
        Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
        Buffer.BlockCopy(KeymatASlice, 0, seed, salt.Length, KeymatASlice.Length);
        Buffer.BlockCopy(KeymatBSlice, 0, seed, salt.Length + KeymatASlice.Length, KeymatBSlice.Length);
        var key = SHA256.HashData(seed);
        var fp = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        if (!string.Equals(fp, KeyFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Отпечаток ключа incy crypt1 не совпал с опубликованным пакетом.");

#if DEBUG
        VerifyPinnedVector(key);
#endif
        return key;
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
}

public readonly record struct IncyCryptPayload(string Url, string? Name);
