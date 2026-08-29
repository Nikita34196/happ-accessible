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
    /// <summary>Hide to tray when the window is closed or minimized, not on app startup.</summary>
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
    /// <summary>On startup, check GitHub Releases for a newer Happ Accessible build.</summary>
    public bool AutoUpdateApp { get; set; } = true;
    public DateTime? LastAppCheckUtc { get; set; }
    public DateTime? LastCoreCheckUtc { get; set; }
    /// <summary>
    /// If the current tunnel dies (or connect fails), try RU / whitelist-bypass servers automatically.
    /// </summary>
    public bool AutoWhitelistFailover { get; set; } = true;
    /// <summary>ipv4_only | prefer_ipv4 | prefer_ipv6 | ipv6_only</summary>
    public string DnsStrategy { get; set; } = "ipv4_only";
    public string DnsRemoteServer { get; set; } = "1.1.1.1";
    public string DnsRemoteFallback { get; set; } = "8.8.8.8";
    /// <summary>Enable the QUIC workaround for xhttp/splithttp links.</summary>
    public bool RejectQuicUdp443 { get; set; } = true;
    /// <summary>Proactively restart tunnel every N minutes (0 = off).</summary>
    public int SessionRefreshMinutes { get; set; } = 90;
    /// <summary>Block direct traffic when VPN session drops unexpectedly.</summary>
    public bool KillSwitchEnabled { get; set; }
    /// <summary>Chrome Secure DNS hint shown once when using system proxy.</summary>
    public bool DoHHintShown { get; set; }

    /// <summary>Remnawave panel base URL for in-app admin (e.g. https://host.sslip.io).</summary>
    public string? RemnawavePanelUrl { get; set; }
    /// <summary>Remnawave API bearer token (stored locally only).</summary>
    public string? RemnawaveApiToken { get; set; }

    /// <summary>Custom display names keyed by RawUri.</summary>
    public Dictionary<string, string> ServerNameOverrides { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Favorite servers by RawUri.</summary>
    public HashSet<string> FavoriteServerUris { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Unix UTC seconds of last successful connect per RawUri.</summary>
    public Dictionary<string, long> ServerLastSuccessUtc { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Last successful subscription fetch (UTC).</summary>
    public DateTimeOffset? SubscriptionLastUpdateUtc { get; set; }
    /// <summary>Upload bytes from subscription-userinfo header.</summary>
    public long? SubscriptionUploadBytes { get; set; }
    /// <summary>Download bytes from subscription-userinfo header.</summary>
    public long? SubscriptionDownloadBytes { get; set; }
    /// <summary>Total traffic quota bytes (0 = unlimited / unknown).</summary>
    public long? SubscriptionTotalBytes { get; set; }
    /// <summary>Unix expiry from subscription-userinfo (seconds).</summary>
    public long? SubscriptionExpireUnix { get; set; }
    /// <summary>Optional profile title from the subscription response.</summary>
    public string? SubscriptionProfileTitle { get; set; }
    /// <summary>Optional support URL from the subscription response.</summary>
    public string? SubscriptionSupportUrl { get; set; }
    /// <summary>Provider's requested refresh interval in hours.</summary>
    public int? SubscriptionProfileUpdateIntervalHours { get; set; }

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
            AppSettings loaded;
            if (!File.Exists(SettingsPath))
            {
                loaded = new AppSettings();
            }
            else
            {
                var json = File.ReadAllText(SettingsPath);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }

            loaded.ServerNameOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            loaded.FavoriteServerUris ??= new HashSet<string>(StringComparer.Ordinal);
            loaded.ServerLastSuccessUtc ??= new Dictionary<string, long>(StringComparer.Ordinal);
            ProtectedSettingsStore.LoadInto(loaded);
            DeviceHwidService.EnsureHwid(loaded);
            return loaded;
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
            var protectedOk = ProtectedSettingsStore.SaveFrom(this);

            var sub = SubscriptionInput;
            var token = RemnawaveApiToken;
            var panel = RemnawavePanelUrl;
            if (protectedOk)
            {
                SubscriptionInput = "";
                RemnawaveApiToken = null;
                RemnawavePanelUrl = null;
            }

            try
            {
                var dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            finally
            {
                SubscriptionInput = sub;
                RemnawaveApiToken = token;
                RemnawavePanelUrl = panel;
            }
        }
    }
}
