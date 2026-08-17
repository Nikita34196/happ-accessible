namespace HappAccessible.Services;

/// <summary>
/// Detects Happ encrypted links. Decryption keys are proprietary to Happ
/// and are not available without reverse-engineering the official app —
/// which this project intentionally does not do.
/// </summary>
public static class CryptLinkHandler
{
    public static bool IsHappCrypt(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var t = input.Trim();
        return t.StartsWith("happ://crypt", StringComparison.OrdinalIgnoreCase)
               || t.Contains("happ://crypt", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExplainLimitation() =>
        "Обнаружена зашифрованная ссылка Happ (happ://crypt…). " +
        "Ключ расшифровки встроен только в официальный Happ; этот клиент его не извлекает. " +
        "Варианты: 1) попросить у провайдера открытую подписку (vless/vmess/trojan/ss/hy2); " +
        "2) вставить сюда уже расшифрованный список серверов (текст/base64); " +
        "3) пользоваться официальным Happ для crypt-ссылок. " +
        "Запрос доступности Windows-Happ: https://issues.happ.su/";
}
