using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace HappAccessible.Services;

/// <summary>Detects network changes and system resume to trigger faster reconnect checks.</summary>
public sealed class NetworkChangeMonitor : IDisposable
{
    private readonly object _gate = new();
    private DateTime _lastSignalUtc = DateTime.MinValue;
    private int _backoffIndex;
    private static readonly TimeSpan[] Backoffs =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    public event Action<string>? RecoverySuggested;

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void ResetBackoff() => _backoffIndex = 0;

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Signal("изменение сети");

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
            Signal("сеть снова доступна");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Signal("выход из сна");
    }

    private void Signal(string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var minGap = Backoffs[Math.Min(_backoffIndex, Backoffs.Length - 1)];
            if (now - _lastSignalUtc < minGap)
                return;
            _lastSignalUtc = now;
            if (_backoffIndex < Backoffs.Length - 1)
                _backoffIndex++;
        }

        AppLogService.Info("NetworkChangeMonitor: " + reason);
        RecoverySuggested?.Invoke(reason);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
