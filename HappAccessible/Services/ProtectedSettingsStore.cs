using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappAccessible.Services;

/// <summary>DPAPI-protected secrets separate from plain settings.json.</summary>
public sealed class ProtectedSettingsStore
{
    private sealed class Payload
    {
        public string? SubscriptionInput { get; set; }
        public string? RemnawaveApiToken { get; set; }
        public string? RemnawavePanelUrl { get; set; }
    }

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible",
            "secrets.dat");

    public static void LoadInto(AppSettings settings)
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            var protectedBytes = File.ReadAllBytes(StorePath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<Payload>(jsonBytes);
            if (payload is null)
                return;

            if (string.IsNullOrWhiteSpace(settings.SubscriptionInput)
                && !string.IsNullOrWhiteSpace(payload.SubscriptionInput))
                settings.SubscriptionInput = payload.SubscriptionInput;
            if (string.IsNullOrWhiteSpace(settings.RemnawaveApiToken)
                && !string.IsNullOrWhiteSpace(payload.RemnawaveApiToken))
                settings.RemnawaveApiToken = payload.RemnawaveApiToken;
            if (string.IsNullOrWhiteSpace(settings.RemnawavePanelUrl)
                && !string.IsNullOrWhiteSpace(payload.RemnawavePanelUrl))
                settings.RemnawavePanelUrl = payload.RemnawavePanelUrl;
        }
        catch
        {
            // ignore — fall back to plain settings fields
        }
    }

    public static bool SaveFrom(AppSettings settings)
    {
        try
        {
            var payload = new Payload
            {
                SubscriptionInput = settings.SubscriptionInput,
                RemnawaveApiToken = settings.RemnawaveApiToken,
                RemnawavePanelUrl = settings.RemnawavePanelUrl
            };
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            var tmp = StorePath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, StorePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Error("Не удалось сохранить защищённые настройки", ex);
            return false;
        }
    }
}
