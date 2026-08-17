using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

public sealed class AppSettings
{
    public string SubscriptionInput { get; set; } = "";
    public bool UseSystemProxy { get; set; } = true;
    public bool UseTun { get; set; }
    /// <summary>gvisor (default) | mixed | system — TUN network stack.</summary>
    public string TunStack { get; set; } = "gvisor";
    /// <summary>Local mixed inbound port (default 2080).</summary>
    public int MixedPort { get; set; } = 2080;
    public bool AutoConnect { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public string? LastServerUri { get; set; }
    public string? LastServerName { get; set; }
    /// <summary>Stable device id sent as x-hwid (Happ/Remnawave compatible).</summary>
    public string? DeviceHwid { get; set; }
    /// <summary>Optional User-Agent override; if empty, common clients are tried automatically.</summary>
    public string? CustomUserAgent { get; set; }
    /// <summary>Last User-Agent that successfully returned parseable servers.</summary>
    public string? LastSuccessfulUserAgent { get; set; }
    /// <summary>global | bypass-ru | proxy-list | bypass-list | app-proxy | app-bypass</summary>
    public string RoutingMode { get; set; } = "global";
    /// <summary>Domains one per line for proxy-list / bypass-list / optional extras for bypass-ru.</summary>
    public string DomainList { get; set; } = "";
    /// <summary>Process names one per line (chrome.exe) for app-proxy / app-bypass.</summary>
    public string AppList { get; set; } = "";
    /// <summary>Periodically re-fetch the subscription URL.</summary>
    public bool AutoUpdateSubscription { get; set; } = true;
    /// <summary>Hours between subscription refreshes (1–168).</summary>
    public int AutoUpdateIntervalHours { get; set; } = 6;
    /// <summary>auto | sing-box | xray</summary>
    public string ProxyCore { get; set; } = "auto";
    /// <summary>On startup, check GitHub releases and download newer cores when idle.</summary>
    public bool AutoUpdateCores { get; set; } = true;
    public DateTime? LastCoreCheckUtc { get; set; }
    /// <summary>
    /// If the current tunnel dies (or connect fails), try RU / whitelist-bypass servers automatically.
    /// </summary>
    public bool AutoWhitelistFailover { get; set; } = true;

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible",
            "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        lock (SaveLock)
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Copy(tmp, SettingsPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
