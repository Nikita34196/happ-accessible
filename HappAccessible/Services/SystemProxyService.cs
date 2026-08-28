using HappAccessible.Models;
using Microsoft.Win32;

namespace HappAccessible.Services;

public sealed class SystemProxyService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private bool _weEnabled;
    private int? _prevEnable;
    private string? _prevServer;
    private string? _prevOverride;
    private bool _hadOverride;
    private string? _ourServer;

    /// <summary>
    /// If a previous crash left our proxy enabled, turn it off only when session marker matches.
    /// </summary>
    public void ClearStaleOwnedProxy(params int[] knownPorts)
    {
        try
        {
            var session = ProxySessionStore.TryLoad();
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null)
                return;

            var enabled = key.GetValue("ProxyEnable") as int? ?? 0;
            var server = key.GetValue("ProxyServer") as string ?? "";
            if (enabled != 1)
            {
                ProxySessionStore.Clear();
                return;
            }

            if (session is not null
                && string.Equals(server, session.ProxyServer, StringComparison.OrdinalIgnoreCase))
            {
                RestorePrevious(key, session.PrevEnable, session.PrevServer, session.PrevOverride, session.HadOverride);
                ProxySessionStore.Clear();
                NotifyChanged();
                return;
            }

            var ports = knownPorts.Length > 0
                ? knownPorts
                : [EngineOptions.DefaultMixedPort, 2080, 10808];
            var ours = ports.Any(p =>
                server.Contains($"127.0.0.1:{p}", StringComparison.OrdinalIgnoreCase)
                || server.Equals($"localhost:{p}", StringComparison.OrdinalIgnoreCase));

            if (!ours)
                return;

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            ProxySessionStore.Clear();
            NotifyChanged();
        }
        catch
        {
            // ignore
        }
    }

    public void Enable(string host, int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                       ?? throw new InvalidOperationException("Не удалось открыть настройки прокси Windows.");

        _prevEnable = key.GetValue("ProxyEnable") as int?;
        _prevServer = key.GetValue("ProxyServer") as string;
        _prevOverride = key.GetValue("ProxyOverride") as string;
        _hadOverride = _prevOverride is not null;

        _ourServer = $"{host}:{port}";

        ProxySessionStore.Save(new ProxySessionStore.Session
        {
            ProxyServer = _ourServer,
            PrevEnable = _prevEnable,
            PrevServer = _prevServer,
            PrevOverride = _prevOverride,
            HadOverride = _hadOverride
        });

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", _ourServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>", RegistryValueKind.String);
        _weEnabled = true;

        NotifyChanged();
    }

    public void DisableIfOwned()
    {
        if (!_weEnabled)
            return;

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null)
            return;

        RestorePrevious(key, _prevEnable, _prevServer, _prevOverride, _hadOverride);

        _weEnabled = false;
        _ourServer = null;
        ProxySessionStore.Clear();
        NotifyChanged();
    }

    public void RefreshIfOwned()
    {
        if (!_weEnabled || string.IsNullOrEmpty(_ourServer))
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null)
                return;

            var enabled = key.GetValue("ProxyEnable") as int? ?? 0;
            var server = key.GetValue("ProxyServer") as string ?? "";
            if (enabled != 1 || !string.Equals(server, _ourServer, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", _ourServer, RegistryValueKind.String);
                key.SetValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>", RegistryValueKind.String);
            }

            NotifyChanged();
        }
        catch
        {
            // ignore
        }
    }

    private static void RestorePrevious(
        RegistryKey key,
        int? prevEnable,
        string? prevServer,
        string? prevOverride,
        bool hadOverride)
    {
        if (prevEnable is int en)
            key.SetValue("ProxyEnable", en, RegistryValueKind.DWord);
        else
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);

        if (prevServer is not null)
            key.SetValue("ProxyServer", prevServer, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        if (hadOverride && prevOverride is not null)
            key.SetValue("ProxyOverride", prevOverride, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
    }

    private static void NotifyChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
        }
        catch
        {
            // ignore
        }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
