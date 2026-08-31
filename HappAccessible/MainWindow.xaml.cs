using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HappAccessible.Models;
using HappAccessible.Services;

namespace HappAccessible;

public partial class MainWindow : Window
{
    private readonly SubscriptionFetcher _fetcher = new();
    private readonly SingBoxRunner _runner = new();
    private readonly XrayRunner _xray = new();
    private readonly AmneziaWgRunner _awg = new();
    private readonly SystemProxyService _proxy = new();
    private readonly CoreUpdateService _coreUpdates = new();
    private readonly AppUpdateCoordinator _appUpdates = new();
    private readonly IncyCompatUpdateService _incyCompat = new();
    private readonly SessionHealthMonitor _healthMonitor = new();
    private readonly KillSwitchService _killSwitch = new();
    private readonly List<ServerProfile> _servers = [];
    private readonly AppSettings _settings;
    private TrayService? _tray;
    private bool _exitRequested;
    private bool _loadingUi = true;
    private CancellationTokenSource? _pingCts;
    private ServerProfile? _connectedServer;
    private ProxyCoreKind _activeCore = ProxyCoreKind.SingBox;
    private DispatcherTimer? _subUpdateTimer;
    private DispatcherTimer? _healthTimer;
    private bool _failoverBusy;
    private bool _connectBusy;
    private bool _awgConnected;
    private bool _healthTickRunning;
    private bool _subUpdateTickRunning;
    private int _sessionEpoch; // bumped on Disconnect to cancel in-flight failover/connect follow-ups
    private bool _remnawaveAdminUnlockedThisSession;
    private NetworkChangeMonitor? _networkMonitor;
    private bool _networkRecoveryBusy;
    private bool _sessionRecoveryBusy;
    private bool _stoppingCores;
    private bool _coreUpdateBusy;
    private DateTime _sessionConnectedUtc;
    private bool _showFavoritesOnly;
    private bool _manualDisconnect;
    private string _activeConnectionFingerprint = "";
    private bool _settingsReconnectBusy;
    private List<HappRoutingProfile> _routingProfiles = [];
    private string? _importedProviderName;
    private enum ConnectOutcome { Success, Failed, Busy, Cancelled }

    public static void ActivateExistingInstance()
    {
        if (System.Windows.Application.Current?.MainWindow is not MainWindow window)
            return;
        window.BringToForeground();
    }

    public void BringToForeground()
    {
        if (_tray is not null && !IsVisible)
            _tray.ShowWindow();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public MainWindow()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        // TUN needs admin: relaunch elevated before UI work
        if (_settings.UseTun && !ElevationHelper.IsElevated)
        {
            if (ElevationHelper.TryRelaunchElevated())
            {
                _exitRequested = true;
                // Do not CaptureSettingsFromUi — checkboxes not applied yet; would clear UseTun
                System.Windows.Application.Current.Shutdown();
                return;
            }

            _settings.UseTun = false;
            PersistSettings();
        }

        _loadingUi = true;
        SystemProxyCheck.IsChecked = _settings.UseSystemProxy;
        TunCheck.IsChecked = _settings.UseTun && ElevationHelper.IsElevated;
        // Prefer proxy-only when both were saved on (TUN + system proxy fights)
        if (TunCheck.IsChecked == true && _settings.UseSystemProxy)
        {
            SystemProxyCheck.IsChecked = false;
            _settings.UseSystemProxy = false;
            PersistSettings();
        }
        AutoConnectCheck.IsChecked = _settings.AutoConnect;
        StartMinimizedCheck.IsChecked = _settings.StartMinimizedToTray;
        AutoUpdateSubCheck.IsChecked = _settings.AutoUpdateSubscription;
        AutoWhitelistCheck.IsChecked = _settings.AutoWhitelistFailover;
        DomainListBox.Text = _settings.DomainList ?? "";
        AppListBox.Text = _settings.AppList ?? "";
        MixedPortBox.Text = EngineOptions.ClampPort(_settings.MixedPort).ToString();
        SelectTunStack(_settings.TunStack);
        SelectProxyCore(_settings.ProxyCore);
        AutoUpdateCoresCheck.IsChecked = _settings.AutoUpdateCores;
        AutoUpdateAppCheck.IsChecked = _settings.AutoUpdateApp;
        if (KillSwitchCheck is not null)
            KillSwitchCheck.IsChecked = _settings.KillSwitchEnabled;
        SelectRoutingMode(_settings.RoutingMode);
        RefreshRoutingProfileBox();
        SelectRoutingProfile(_settings.ActiveRoutingProfileId);
        UpdateDomainListVisibility();
        UpdateSubscriptionInfo();
        _loadingUi = false;

        if (_settings.UseTun && ElevationHelper.IsElevated)
            Title = "Happ Accessible (администратор)";

        _tray = new TrayService(this);
        _tray.SnapshotProvider = BuildTraySnapshot;
        _tray.ShowRequested += () => Dispatcher.Invoke(() => _tray.ShowWindow());
        _tray.ConnectToggleRequested += () => Dispatcher.InvokeAsync(async () => await ConnectToggleAsync());
        _tray.CheckConnectionRequested += () => Dispatcher.InvokeAsync(async () => await CheckConnectionAsync());
        _tray.PingAllRequested += () => Dispatcher.InvokeAsync(async () => await PingAllServersAsync());
        _tray.RefreshSubscriptionRequested += () => Dispatcher.InvokeAsync(async () =>
        {
            await LoadSubscriptionAsync(announce: true);
            _tray?.Notify($"Серверов в списке: {_servers.Count}");
        });
        _tray.ServerConnectRequested += uri => Dispatcher.InvokeAsync(async () =>
            await ConnectServerFromTrayAsync(uri));
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _tray.SetTooltip("Happ Accessible — не подключено");
        UpdateConnectToggleUi();

        _runner.CoreExited += OnCoreProcessExited;
        _xray.CoreExited += OnCoreProcessExited;

        // Recover from crash: leftover system proxy / AmneziaWG tunnel
        _proxy.ClearStaleOwnedProxy(_settings.MixedPort, EngineOptions.DefaultMixedPort, 10808);
        // Remove rules left by a crashed process before attempting a new session.
        _killSwitch.Disarm();
        TryDeleteRuntimeSecret(_runner.ConfigPath);
        TryDeleteRuntimeSecret(_xray.ConfigPath);
        AmneziaWgConfigStore.RemoveActiveConfig();
        try
        {
            if (_awg.IsTunnelRunning)
                await _awg.DisconnectAsync();
        }
        catch
        {
            // ignore — may need UAC later
        }

        RestartBackgroundTimers();
        StartNetworkMonitor();
        MergeAmneziaConfigsIntoList();

        if (!string.IsNullOrWhiteSpace(_settings.SubscriptionInput))
        {
            // Plain AWG conf pasted into the box
            if (AmneziaWgConfigStore.LooksLikeConf(_settings.SubscriptionInput))
            {
                try
                {
                    AmneziaWgConfigStore.ImportFromText(_settings.SubscriptionInput, "pasted");
                    MergeAmneziaConfigsIntoList();
                    SelectLastServer();
                    SetStatus($"Импортирован AmneziaWG-конфиг. В списке серверов: {_servers.Count}.");
                }
                catch (Exception ex)
                {
                    SetStatus("Не удалось разобрать AmneziaWG-конфиг: " + ex.Message);
                }
            }
            else
            {
                await LoadSubscriptionAsync(announce: false);
            }

            if (_settings.AutoConnect && _servers.Count > 0)
            {
                SelectLastServer();
                SetStatus("Автоподключение…");
                await ConnectAsync(allowFailover: true);
            }
            else if (_servers.Count > 0)
            {
                SelectLastServer();
                var wl = _servers.Count(s => s.IsWhitelistBypass);
                var awg = _servers.Count(s => s.Protocol == "amneziawg");
                SetStatus($"Загружено серверов: {_servers.Count}" +
                          (wl > 0 ? $", обход БС: {wl}" : "") +
                          (awg > 0 ? $", AmneziaWG: {awg}" : "") +
                          ". Автоподключение выключено.");
            }
        }
        else
        {
            MergeAmneziaConfigsIntoList();
            if (_servers.Count > 0)
                SelectLastServer();
        }

        _ = CheckAndUpdateCoresOnStartupAsync();
        _ = CheckAndUpdateAppOnStartupAsync();
        _ = RefreshIncyCompatAsync(force: false);
    }

    private async Task RefreshIncyCompatAsync(bool force)
    {
        try
        {
            if (force)
                SetStatus("Обновление совместимости INCY…");
            var notes = await _incyCompat.RefreshAsync(force);
            if (force)
            {
                SetStatus(string.IsNullOrWhiteSpace(notes)
                    ? "Совместимость INCY уже актуальна."
                    : "Совместимость INCY обновлена: " + notes);
            }
        }
        catch (Exception ex)
        {
            if (force)
                SetStatus("Не удалось обновить совместимость INCY: " + ex.Message);
            else
                AppLogService.Error("INCY compat refresh on startup failed", ex);
        }
    }

    private async void MenuRefreshIncyCompat_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshIncyCompatAsync(force: true);
    }

    private async Task CheckAndUpdateAppOnStartupAsync()
    {
        try
        {
            if (_settings.LastAppCheckUtc is { } last
                && DateTime.UtcNow - last < TimeSpan.FromHours(6))
                return;

            SetStatus("Проверка обновления приложения (GitHub)…");
            var info = await _appUpdates.CheckAsync();
            _settings.LastAppCheckUtc = DateTime.UtcNow;
            PersistSettings();

            if (!info.UpdateAvailable)
                return;

            if (!_settings.AutoUpdateApp)
            {
                SetStatus(
                    $"Доступна версия {info.LatestVersion} (сейчас {info.CurrentVersion}). " +
                    "Справка → Проверить обновление приложения.");
                _tray?.Notify($"Доступно обновление {info.LatestVersion}");
                return;
            }

            if (_connectedServer is not null || _connectBusy || _awgConnected)
            {
                SetStatus(
                    $"Доступна версия {info.LatestVersion} (отложено: есть подключение). " +
                    "Отключитесь и: Справка → Проверить обновление приложения.");
                return;
            }

            await ApplyAppUpdateAsync(info, silent: true);
        }
        catch (Exception ex)
        {
            SetStatus("Проверка приложения не удалась: " + ex.Message);
        }
    }

    private async void MenuCheckAppUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Проверка обновления приложения…");
            var info = await CheckAppUpdateWithVpnFallbackAsync();
            _settings.LastAppCheckUtc = DateTime.UtcNow;
            PersistSettings();
            if (!info.UpdateAvailable)
            {
                SetStatus(string.Equals(info.CurrentVersion, info.LatestVersion, StringComparison.OrdinalIgnoreCase)
                    ? $"Уже последняя версия: {info.CurrentVersion}."
                    : $"Обновление не предложено. У вас {info.CurrentVersion}, на GitHub {info.LatestVersion}. Скачайте вручную: {info.ReleaseUrl}");
                return;
            }

            await ApplyAppUpdateAsync(info, silent: false);
        }
        catch (Exception ex)
        {
            var hint = DirectHttp.IsSslOrTransportFailure(ex)
                ? " Отключите VPN или скачайте с GitHub Releases вручную."
                : "";
            SetStatus("Обновление приложения: " + ex.Message + hint);
        }
    }

    private async Task<AppReleaseInfo> CheckAppUpdateWithVpnFallbackAsync()
    {
        try
        {
            return await _appUpdates.CheckAsync();
        }
        catch (Exception ex) when (IsVpnConnected && DirectHttp.IsSslOrTransportFailure(ex))
        {
            SetStatus("GitHub недоступен через VPN — отключаю туннель для проверки обновления…");
            await DisconnectAsync(manual: false);
            await Task.Delay(1500);
            return await _appUpdates.CheckAsync();
        }
    }

    private async void MenuWhatIsNew_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Загружаю список изменений версии…", important: false);
            AppReleaseInfo info;
            try
            {
                info = await _appUpdates.CheckAsync();
            }
            catch
            {
                var localNotes = AppUpdateService.GetLocalChangeLog();
                ShowChangeLog(AppUpdateService.GetCurrentVersion(), localNotes, offline: true);
                return;
            }

            var version = info.UpdateAvailable ? info.LatestVersion : info.CurrentVersion;
            var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? AppUpdateService.GetLocalChangeLog()
                : info.ReleaseNotes.Trim();

            if (notes.Length > 12000)
                notes = notes[..12000] + "\n\n…";

            if (string.IsNullOrWhiteSpace(notes))
                notes = $"Для версии {version} подробный список изменений пока не опубликован.\n\n" +
                        $"Открыть страницу релиза: {info.ReleaseUrl}";

            ShowChangeLog(version, notes, offline: false);
            SetStatus($"Список изменений версии {version} показан.", important: false);
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось загрузить список изменений: " + ex.Message);
        }
    }

    private void ShowChangeLog(string version, string notes, bool offline)
    {
        if (notes.Length > 12000)
            notes = notes[..12000] + "\n\n…";

        System.Windows.MessageBox.Show(
            this,
            $"Happ Accessible {version}\n\n{notes}",
            offline ? "Что нового (локальная копия)" : "Что нового",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        SetStatus(
            offline
                ? "Показан локальный список изменений."
                : $"Список изменений версии {version} показан.",
            important: false);
    }

    private async Task ApplyAppUpdateAsync(AppReleaseInfo info, bool silent)
    {
        if (!silent)
        {
            var confirm = System.Windows.MessageBox.Show(
                this,
                $"Доступна версия {info.LatestVersion} (сейчас {info.CurrentVersion}).\n\n" +
                "Установить сейчас? Приложение перезапустится.",
                "Обновление Happ Accessible",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var progress = new Progress<string>(msg => SetStatus(msg, important: !silent));
        var disconnectedForUpdate = false;
        try
        {
            await ApplyAppUpdateCoreAsync(info, silent, progress);
        }
        catch (Exception ex) when (!disconnectedForUpdate
                                   && IsVpnConnected
                                   && DirectHttp.IsSslOrTransportFailure(ex))
        {
            SetStatus("Обновление через VPN не удалось — отключаю туннель и пробую снова…");
            await DisconnectAsync(manual: false);
            disconnectedForUpdate = true;
            await Task.Delay(1500);
            await ApplyAppUpdateCoreAsync(info, silent, progress);
        }
        catch (Exception ex)
        {
            ReportAppUpdateFailure(ex, silent);
        }
    }

    private async Task ApplyAppUpdateCoreAsync(
        AppReleaseInfo info, bool silent, IProgress<string> progress)
    {
        await _appUpdates.ApplyAsync(info, silent, progress);
        var msg = $"Обновление до {info.LatestVersion}. Приложение перезапустится.";
        SetStatus(msg, important: !silent);
        _tray?.Notify(msg);
        _exitRequested = true;
        Cleanup(persistFromUi: false);
        System.Windows.Application.Current.Shutdown();
    }

    private void ReportAppUpdateFailure(Exception ex, bool silent)
    {
        var hint = DirectHttp.IsSslOrTransportFailure(ex)
            ? " Отключите VPN и повторите, или скачайте установщик вручную с GitHub Releases."
            : "";
        SetStatus("Обновление: " + ex.Message + hint);
        if (!silent)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message + hint,
                "Обновление",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task CheckAndUpdateCoresOnStartupAsync()
    {
        if (_coreUpdateBusy)
            return;

        _coreUpdateBusy = true;
        try
        {
            try
            {
                var bootProgress = new Progress<string>(ReportCoreProgress);
                await _runner.EnsureBinaryAsync(progress: bootProgress);
            }
            catch (Exception ex)
            {
                AppLogService.Warn("Первичная загрузка sing-box: " + ex.Message);
            }

            var state = CoreVersionsState.Load();
            var localSb = state.SingBox ?? CoreUpdateService.NormalizeTag(_runner.CoreVersion);
            var localXr = state.Xray;
            var localAwg = state.AmneziaWg ?? _awg.InstalledVersion;

            SetStatus("Проверка обновлений ядер (GitHub)…");
            AppLogService.Info("Проверка обновлений ядер…");
            var infos = await _coreUpdates.CheckAllAsync(localSb, localXr, localAwg);
            _settings.LastCoreCheckUtc = DateTime.UtcNow;
            PersistSettings();

            var updates = infos.Where(i => i.UpdateAvailable && !string.IsNullOrEmpty(i.DownloadUrl)).ToList();
            if (updates.Count == 0)
            {
                string Ver(CoreReleaseInfo i) =>
                    string.IsNullOrEmpty(i.LocalVersion) ? i.RemoteVersion : i.LocalVersion;
                var ok =
                    $"Ядра актуальны: sing-box {Ver(infos[0])}, Xray {Ver(infos[1])}, AmneziaWG {Ver(infos[2])}.";
                SetStatus(ok);
                AppLogService.Info(ok);
                return;
            }

            var summary = string.Join("; ",
                updates.Select(u =>
                    $"{u.Id} {(string.IsNullOrEmpty(u.LocalVersion) ? "—" : u.LocalVersion)} → {u.RemoteVersion}"));

            if (!_settings.AutoUpdateCores)
            {
                SetStatus($"Доступны обновления ядер: {summary}. Меню Справка → Проверить обновления ядер.");
                _tray?.Notify($"Доступны обновления ядер: {summary}");
                AppLogService.Info("Обновления ядер доступны (автовыкл): " + summary);
                return;
            }

            if (_connectedServer is not null || _connectBusy || _awgConnected)
            {
                SetStatus($"Доступны обновления ядер (отложено, есть подключение): {summary}.");
                _tray?.Notify("Обновление ядер отложено — есть активное подключение.");
                AppLogService.Info("Обновление ядер отложено: " + summary);
                return;
            }

            SetStatus($"Обновляю ядра: {summary}…");
            _tray?.Notify($"Загрузка ядер: {summary}");
            AppLogService.Info("Начало обновления ядер: " + summary);

            foreach (var u in updates)
            {
                var progress = new Progress<string>(ReportCoreProgress);
                if (u.Id == "sing-box")
                    await _runner.EnsureBinaryAsync(forceUpdate: true, downloadUrl: u.DownloadUrl,
                        expectedVersion: u.RemoteVersion, progress: progress);
                else if (u.Id == "xray")
                    await _xray.EnsureBinaryAsync(forceUpdate: true, downloadUrl: u.DownloadUrl,
                        expectedVersion: u.RemoteVersion, progress: progress);
                else if (u.Id == "amneziawg")
                    await _awg.EnsureBinaryAsync(forceUpdate: true, downloadUrl: u.DownloadUrl,
                        expectedVersion: u.RemoteVersion, progress: progress);
            }

            var done = $"Ядра обновлены: {summary}.";
            SetStatus(done);
            _tray?.Notify(done);
            AppLogService.Info(done);
        }
        catch (Exception ex)
        {
            SetStatus("Проверка ядер не удалась: " + ex.Message);
            AppLogService.Error("Проверка/обновление ядер не удалась", ex);
            _tray?.Notify("Ошибка обновления ядер — см. Логи");
        }
        finally
        {
            _coreUpdateBusy = false;
        }
    }

    private void ReportCoreProgress(string message)
    {
        SetStatus(message);
        AppLogService.Info(message);
        // Balloon on start/finish of a core, not every percent tick
        if (message.StartsWith("Скачиваю ", StringComparison.Ordinal)
            || message.StartsWith("Обновляю ", StringComparison.Ordinal)
            || message.StartsWith("Готово:", StringComparison.Ordinal))
            _tray?.Notify(message);
    }

    private void LogsButton_OnClick(object sender, RoutedEventArgs e) => OpenLogs();

    private void MenuOpenLogs_OnClick(object sender, RoutedEventArgs e) => OpenLogs();

    private void MenuSessionJournal_OnClick(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            SessionJournalService.FormatRecentForDisplay(),
            "Журнал сессии",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenLogs()
    {
        try
        {
            AppLogService.EnsureLogFile();
            AppLogService.Info("Пользователь открыл логи.");
            AppLogService.OpenInExplorer();
            SetStatus($"Логи: {AppLogService.LogPath}");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось открыть логи: " + ex.Message);
            AppLogService.Error("Не удалось открыть логи", ex);
        }
    }

    private void RestartBackgroundTimers()
    {
        _subUpdateTimer?.Stop();
        _healthTimer?.Stop();

        var hours = Math.Clamp(_settings.AutoUpdateIntervalHours, 1, 168);
        _subUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(hours)
        };
        _subUpdateTimer.Tick += async (_, _) =>
        {
            if (_subUpdateTickRunning)
                return;
            _subUpdateTickRunning = true;
            try { await AutoUpdateSubscriptionTickAsync(); }
            finally { _subUpdateTickRunning = false; }
        };
        if (_settings.AutoUpdateSubscription)
            _subUpdateTimer.Start();

        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45)
        };
        _healthTimer.Tick += async (_, _) =>
        {
            if (_healthTickRunning)
                return;
            _healthTickRunning = true;
            try { await HealthCheckTickAsync(); }
            finally { _healthTickRunning = false; }
        };
        _healthTimer.Start();
    }

    private void StartNetworkMonitor()
    {
        _networkMonitor?.Dispose();
        _networkMonitor = new NetworkChangeMonitor();
        _networkMonitor.RecoverySuggested += reason =>
        {
            Dispatcher.InvokeAsync(async () => await HandleNetworkRecoveryAsync(reason));
        };
        _networkMonitor.Start();
    }

    private async Task HandleNetworkRecoveryAsync(string reason)
    {
        if (_manualDisconnect || _stoppingCores
            || _networkRecoveryBusy || _connectBusy || _failoverBusy || !IsVpnConnected)
            return;

        _networkRecoveryBusy = true;
        var epoch = _sessionEpoch;
        try
        {
            SetStatus($"Сеть: {reason}. Проверяю соединение…", important: false);
            await HealthCheckTickAsync(forceImmediate: true);
            if (epoch != _sessionEpoch || _manualDisconnect || _stoppingCores)
                return;
        }
        finally
        {
            _networkRecoveryBusy = false;
            _networkMonitor?.ResetBackoff();
        }
    }

    private void OnCoreProcessExited()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (_stoppingCores || _connectBusy || _failoverBusy || _sessionRecoveryBusy
                || _coreUpdateBusy || !IsVpnConnected || _awgConnected)
                return;
            await HandleSessionFailureAsync("ядро прокси неожиданно завершилось");
        });
    }

    private async Task AutoUpdateSubscriptionTickAsync()
    {
        if (!_settings.AutoUpdateSubscription)
            return;
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionInput))
            return;
        if (_connectBusy || _failoverBusy)
            return;

        var previousUri = _connectedServer?.RawUri;
        var epoch = _sessionEpoch;
        await LoadSubscriptionAsync(announce: false, quiet: true);

        if (epoch != _sessionEpoch)
            return;
        if (previousUri is null)
            return;
        // User may have switched servers while we were fetching
        if (_connectedServer is null
            || !string.Equals(_connectedServer.RawUri, previousUri, StringComparison.Ordinal))
            return;
        if (_connectBusy || _failoverBusy)
            return;

        if (_servers.All(s => s.RawUri != previousUri))
        {
            SelectLastServer();
            SetStatus("Подписка обновилась, прежний сервер исчез — переподключаюсь…");
            await ConnectAsync(allowFailover: true);
        }
    }

    private async Task HealthCheckTickAsync(bool forceImmediate = false)
    {
        var ctx = new HealthTickContext
        {
            IsBusy = _connectBusy || _failoverBusy,
            IsConnected = IsVpnConnected,
            IsAmnezia = _awgConnected || _connectedServer?.Protocol == "amneziawg",
            AwgTunnelRunning = _awg.IsTunnelRunning,
            CoreRunning = _activeCore == ProxyCoreKind.Xray ? _xray.IsRunning : _runner.IsRunning,
            SystemProxyEnabled = SystemProxyCheck.IsChecked == true,
            MixedPort = GetEngineOptions().MixedPort,
            SessionRefreshMinutes = _settings.SessionRefreshMinutes,
            SessionConnectedUtc = _sessionConnectedUtc,
            RoutingModeTag = GetSelectedRoutingModeTag(),
            ConnectedServer = _connectedServer,
            RefreshProxy = () => _proxy.RefreshIfOwned(),
            ProbeFullSessionAsync = async () =>
            {
                if (_activeCore == ProxyCoreKind.Xray)
                    return await _xray.ProbeSessionHealthAsync();
                return await _runner.ProbeSessionHealthAsync();
            },
            ProbeAwgHandshakeAsync = () => _awg.HasHandshakeAsync()
        };

        var result = await _healthMonitor.RunTickAsync(ctx, forceImmediate);
        if (_connectBusy || _failoverBusy || _manualDisconnect || _stoppingCores || !IsVpnConnected)
            return;

        switch (result.ResultKind)
        {
            case HealthTickResult.Kind.RefreshSession:
                await RefreshSessionAsync();
                break;
            case HealthTickResult.Kind.Retry:
                SetStatus($"Проверка связи: {result.Detail}. Повторю…", important: false);
                break;
            case HealthTickResult.Kind.Failure:
                await HandleSessionFailureAsync(result.Detail ?? "сессия не отвечает");
                break;
        }
    }

    private async Task RefreshSessionAsync()
    {
        if (_connectBusy || _failoverBusy || _connectedServer is null)
            return;

        var server = _connectedServer;
        _sessionConnectedUtc = DateTime.UtcNow;

        SetStatus($"Профилактика: обновляю туннель «{server.Name}»…", important: false);
        _tray?.Notify($"Обновление туннеля: {server.Name}");
        SessionJournalService.Record($"Профилактический refresh: {server.Name}.");

        var epoch = _sessionEpoch;
        var outcome = await TryConnectServerAsync(server);
        if (outcome == ConnectOutcome.Success)
        {
            SetStatus($"Туннель обновлён: {server.Name}.", important: false);
            return;
        }

        if (epoch == _sessionEpoch)
            await HandleSessionFailureAsync("Не удалось обновить сессию после проактивного refresh.");
    }

    private async Task HandleSessionFailureAsync(string reason)
    {
        if (_connectBusy || _failoverBusy || _sessionRecoveryBusy || _connectedServer is null)
            return;

        _sessionRecoveryBusy = true;
        try
        {
            var failed = _connectedServer;
            var failedUri = failed.RawUri;
            SessionJournalService.Record($"Сбой сессии: {reason} ({failed.Name}).");

            if (_settings.AutoWhitelistFailover && failed.Protocol != "amneziawg")
            {
                SetStatus($"Проверка связи: {reason} ({failed.Name}). Ищу сервер обхода белых списков…");
                await FailoverToWhitelistAsync(excludeUri: failedUri);
                return;
            }

            SetStatus($"Проверка связи: {reason} ({failed.Name}). Переподключаюсь…");
            _tray?.Notify($"Переподключение: {failed.Name}");

            await DisconnectAsync(manual: false);

            var server = _servers.FirstOrDefault(s => s.RawUri == failedUri);
            if (server is null)
                return;

            ServerList.SelectedItem = server;
            await ConnectAsync(allowFailover: false, server);
        }
        finally
        {
            _sessionRecoveryBusy = false;
        }
    }

    private static async Task<bool> ProbeDirectAsync()
    {
        string[] urls =
        [
            "http://www.gstatic.com/generate_204",
            "http://connectivitycheck.gstatic.com/generate_204",
            "http://captive.apple.com/hotspot-detect.html"
        ];

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        foreach (var url in urls)
        {
            try
            {
                using var resp = await client.GetAsync(url);
                if ((int)resp.StatusCode is 204 or 200 or 301 or 302)
                    return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    /// <summary>Wait for routes/DNS/handshake, then probe several times.</summary>
    private async Task<(bool probeOk, bool handshakeOk)> WaitForAwgReadyAsync()
    {
        var handshake = false;
        for (var i = 0; i < 12; i++)
        {
            SetStatus($"AmneziaWG: ожидание рукопожатия… ({i + 1}/12)");
            handshake = await _awg.HasHandshakeAsync();
            if (handshake)
                break;
            if (await ProbeDirectAsync())
                return (true, handshake);
            await Task.Delay(1000);
        }

        for (var i = 0; i < 8; i++)
        {
            SetStatus($"AmneziaWG: проверка интернета… ({i + 1}/8)");
            if (await ProbeDirectAsync())
                return (true, handshake || await _awg.HasHandshakeAsync());
            await Task.Delay(1200);
        }

        handshake = handshake || await _awg.HasHandshakeAsync();
        return (false, handshake);
    }

    private void SelectLastServer()
    {
        if (_servers.Count == 0)
            return;

        var idx = 0;
        if (!string.IsNullOrEmpty(_settings.LastServerUri))
        {
            var found = _servers.FindIndex(s => s.RawUri == _settings.LastServerUri);
            if (found >= 0)
                idx = found;
        }

        ServerList.SelectedIndex = idx;
    }

    private void MergeAmneziaConfigsIntoList()
    {
        // Drop previous awg entries, keep subscription/URI ones
        _servers.RemoveAll(s => s.Protocol == "amneziawg");
        foreach (var cfg in AmneziaWgConfigStore.ListImported())
            _servers.Add(cfg.ToProfile());
        ApplyNameOverrides();
        RefreshServerList();
    }

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = RefreshSubscriptionAsync();
            return;
        }

        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
            return;

        if (e.Key is Key.C or Key.D)
        {
            e.Handled = true;
            _ = ConnectToggleAsync();
            return;
        }

        // Hidden operator entry: not shown in menus for regular users.
        if (e.Key == Key.R)
        {
            e.Handled = true;
            _ = OpenRemnawaveAdminIfUnlockedAsync();
        }
    }

    private async void MenuEditSubscription_OnClick(object sender, RoutedEventArgs e) =>
        await EditSubscriptionAsync();

    private async Task OpenRemnawaveAdminIfUnlockedAsync()
    {
        if (!_remnawaveAdminUnlockedThisSession)
        {
            var pin = PromptPassword(
                "Доступ оператора",
                "Введите PIN управления панелью (только для операторов):");
            if (pin is null)
                return;
            if (!RemnawaveAdminGate.VerifyPin(pin))
            {
                SetStatus("Неверный PIN. Управление панелью недоступно.");
                return;
            }

            _remnawaveAdminUnlockedThisSession = true;
        }

        var dlg = new RemnawaveAdminWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.AppliedSubscriptionUrl))
            await ApplyImportedTextAsync(dlg.AppliedSubscriptionUrl, sourceLabel: "панели Remnawave");
    }

    private string? PromptPassword(string title, string label)
    {
        var box = new System.Windows.Controls.PasswordBox
        {
            MinWidth = 320,
            MinHeight = 28,
            Margin = new Thickness(0, 8, 0, 12)
        };
        AutomationProperties.SetName(box, "PIN управления панелью");
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            MinWidth = 90,
            MinHeight = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            MinWidth = 90,
            MinHeight = 28,
            IsCancel = true
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Password; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        box.Focus();
        return dlg.ShowDialog() == true ? result : null;
    }

    private async void MenuRefreshSubscription_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshSubscriptionAsync();

    private async void MenuImportClipboard_OnClick(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                SetStatus("Буфер обмена пуст.");
                return;
            }

            text = System.Windows.Clipboard.GetText() ?? "";
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось прочитать буфер: " + ex.Message);
            return;
        }

        await ApplyImportedTextAsync(text, sourceLabel: "буфера");
    }

    private async void MenuImportSubscriptionFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт подписки или списка серверов",
            Filter = "Текст / подписка (*.txt;*.json;*.yaml;*.yml)|*.txt;*.json;*.yaml;*.yml|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var text = await File.ReadAllTextAsync(dlg.FileName);
            await ApplyImportedTextAsync(text, sourceLabel: Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка чтения файла: " + ex.Message);
        }
    }

    private void MenuImportAwgFile_OnClick(object sender, RoutedEventArgs e) => ImportAwgFromFiles();

    private void MenuImportAwgClipboard_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                SetStatus("Буфер обмена пуст.");
                return;
            }

            var text = System.Windows.Clipboard.GetText() ?? "";
            if (!AmneziaWgConfigStore.LooksLikeConf(text))
            {
                SetStatus("В буфере нет AmneziaWG-конфига ([Interface]).");
                return;
            }

            AmneziaWgConfigStore.ImportFromText(text, "clipboard");
            MergeAmneziaConfigsIntoList();
            SelectLastServer();
            SetStatus("AmneziaWG-конфиг из буфера добавлен в список.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка импорта AWG: " + ex.Message);
        }
    }

    private void SelectProxyCore(string? value)
    {
        var kind = ProxyCoreKindParser.Parse(value);
        var tag = ProxyCoreKindParser.ToSetting(kind);
        if (ProxyCoreBox is null)
            return;
        foreach (var item in ProxyCoreBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                ProxyCoreBox.SelectedItem = item;
                return;
            }
        }

        ProxyCoreBox.SelectedIndex = 0;
    }

    private string GetSelectedProxyCoreSetting()
    {
        if (ProxyCoreBox?.SelectedItem is ComboBoxItem { Tag: string tag })
            return ProxyCoreKindParser.ToSetting(ProxyCoreKindParser.Parse(tag));
        return "auto";
    }

    private async void MenuCheckCoreUpdates_OnClick(object sender, RoutedEventArgs e)
    {
        await CheckAndUpdateCoresOnStartupAsync();
    }

    private void MenuAbout_OnClick(object sender, RoutedEventArgs e)
    {
        var ver = AppUpdateService.GetCurrentVersion();
        SetStatus(
            $"Happ Accessible {ver}. Ядра: sing-box / Xray / AmneziaWG. " +
            "Ctrl+Shift+C — подключить или отключить. F2 — переименовать сервер. " +
            "ПКМ по значку трея — серверы и проверка связи.");
    }

    private void CheckWhitelistButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_servers.Count == 0)
        {
            SetStatus("Сначала загрузите подписку (Alt → Подписка).");
            return;
        }

        foreach (var s in _servers)
            s.IsWhitelistBypass = ServerClassifier.IsWhitelistBypass(s);

        var wl = ServerClassifier.WhitelistOnly(_servers);
        var awg = _servers.Count(s => s.Protocol == "amneziawg");
        RefreshServerList();

        if (wl.Count == 0)
        {
            SetStatus(
                $"Обход белых списков: в подписке не найдено подходящих серверов " +
                $"(всего {_servers.Count}, AmneziaWG: {awg}). " +
                "Обычно они помечены как RU / bridge / whitelist / «белый список».");
            return;
        }

        var sample = string.Join(", ", wl.Take(5).Select(s => s.Name));
        SetStatus(
            $"Обход белых списков: найдено {wl.Count} из {_servers.Count}. " +
            $"Примеры: {sample}" + (wl.Count > 5 ? "…" : "") +
            ". Автопереключение сможет их использовать.");
    }

    private void ImportAwgFromFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт AmneziaWG / WireGuard",
            Filter = "Конфиги (*.conf)|*.conf|Все файлы (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var imported = 0;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                AmneziaWgConfigStore.ImportFromFile(file);
                imported++;
            }
            catch (Exception ex)
            {
                SetStatus($"Не импортирован {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        MergeAmneziaConfigsIntoList();
        if (imported > 0)
        {
            SelectLastServer();
            SetStatus($"Импортировано AmneziaWG-конфигов: {imported}. Всего в списке: {_servers.Count}.");
        }
    }

    private async Task EditSubscriptionAsync()
    {
        var dlg = new SubscriptionEditorWindow(_settings.SubscriptionInput ?? "")
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true)
            return;

        await ApplyImportedTextAsync(dlg.SubscriptionText ?? "", sourceLabel: "окна подписки");
    }

    private async Task RefreshSubscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionInput))
        {
            SetStatus("Подписка не задана. Alt → Подписка → Изменить ссылку…");
            return;
        }

        if (AmneziaWgConfigStore.LooksLikeConf(_settings.SubscriptionInput))
        {
            SetStatus("Сохранён конфиг, а не ссылка. Alt → Подписка → Изменить ссылку…");
            return;
        }

        await LoadSubscriptionAsync(announce: true);
    }

    private async Task ApplyImportedTextAsync(string text, string sourceLabel)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Пустой текст из " + sourceLabel + ".");
            return;
        }

        try
        {
            if (IncyDeepLink.TryParse(text, out var incy))
            {
                switch (incy.Kind)
                {
                    case IncyLinkKind.VpnControl:
                        SetStatus(IncyDeepLink.DescribeVpnControl(incy.ControlAction));
                        return;
                    case IncyLinkKind.RoutingOff:
                        await ApplyRoutingOffAsync();
                        return;
                    case IncyLinkKind.RoutingProfile:
                        await ImportRoutingPayloadAsync(incy.Payload, activate: true);
                        return;
                    case IncyLinkKind.Crypt1:
                    case IncyLinkKind.Subscription:
                        if (!string.IsNullOrWhiteSpace(incy.ProviderName))
                            _importedProviderName = incy.ProviderName;
                        await ApplyImportedTextAsync(incy.Payload, sourceLabel);
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка ссылки INCY: " + ex.Message);
            return;
        }

        if (HappRoutingImporter.LooksLikeRoutingLink(text)
            || HappRoutingImporter.LooksLikeRoutingJson(text))
        {
            try
            {
                await ImportRoutingPayloadAsync(text, activate: true);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка импорта профиля маршрутизации: " + ex.Message);
            }

            return;
        }

        if (AmneziaWgConfigStore.LooksLikeConf(text))
        {
            try
            {
                AmneziaWgConfigStore.ImportFromText(text, "import");
                if (string.IsNullOrWhiteSpace(_settings.SubscriptionInput)
                    || AmneziaWgConfigStore.LooksLikeConf(_settings.SubscriptionInput))
                    _settings.SubscriptionInput = "";
                PersistSettings();
                UpdateSubscriptionInfo();
                MergeAmneziaConfigsIntoList();
                SelectLastServer();
                SetStatus("AmneziaWG-конфиг из " + sourceLabel + " добавлен в список.");
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка импорта AWG: " + ex.Message);
            }

            return;
        }

        var normalized = SubscriptionFetcher.NormalizeInput(text);
        // Single URL → save as subscription and fetch
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || CryptLinkHandler.IsHappCrypt(normalized))
        {
            ResetSubscriptionMetadata();
            _settings.SubscriptionInput = normalized;
            PersistSettings();
            UpdateSubscriptionInfo();
            await LoadSubscriptionAsync(announce: true);
            return;
        }

        // Raw list of servers
        var parsed = SubscriptionParser.Parse(text);
        if (parsed.Count == 0)
        {
            SetStatus("Не удалось распознать подписку или серверы из " + sourceLabel + ".");
            return;
        }

        foreach (var s in parsed)
            s.IsWhitelistBypass = ServerClassifier.IsWhitelistBypass(s);

        _servers.Clear();
        _servers.AddRange(parsed);
        // Persist raw imports as local subscription content so they survive
        // restart. FetchAsync returns non-URL content unchanged.
        _settings.SubscriptionInput = text;
        ResetSubscriptionMetadata();
        _settings.SubscriptionLastUpdateUtc = DateTimeOffset.UtcNow;
        PersistSettings();
        foreach (var cfg in AmneziaWgConfigStore.ListImported())
            _servers.Add(cfg.ToProfile());
        ApplyNameOverrides();
        RefreshServerList();
        SelectLastServer();
        // Keep previous URL if any; don't overwrite with huge body
        UpdateSubscriptionInfo();
        var wl = _servers.Count(s => s.IsWhitelistBypass);
        SetStatus($"Импорт из {sourceLabel}: {_servers.Count} серверов (обход БС: {wl}).");
    }

    // Legacy button handlers removed — use menu / CheckWhitelist / hotkeys


    private async Task LoadSubscriptionAsync(bool announce, bool quiet = false)
    {
        try
        {
            if (announce)
                SetStatus("Загрузка подписки…");

            var input = SubscriptionFetcher.NormalizeInput(_settings.SubscriptionInput ?? "");
            _settings.SubscriptionInput = input;
            UpdateSubscriptionInfo();
            if (string.IsNullOrWhiteSpace(input))
            {
                if (!quiet)
                    SetStatus("Подписка пуста. Нажмите «Подписка…».");
                return;
            }
            if (CryptLinkHandler.IsHappCrypt(input))
            {
                SetStatus(CryptLinkHandler.ExplainLimitation());
                return;
            }
            if (IncyCryptCodec.IsCryptLink(input))
            {
                SetStatus("Не удалось расшифровать incy://crypt…/. Справка → Обновить совместимость INCY, либо вставьте открытый URL.");
                return;
            }

            string body;
            var fromCache = false;
            try
            {
                body = await _fetcher.FetchAsync(input, _settings);
            }
            catch (Exception fetchEx)
            {
                var cached = SubscriptionFetcher.TryLoadCacheOnly(input);
                if (cached is not null && SubscriptionParser.Parse(cached).Count > 0)
                {
                    body = cached;
                    fromCache = true;
                    if (announce)
                        SetStatus("Сеть недоступна или лимит запросов. Загружаю сохранённый кэш. " + fetchEx.Message);
                }
                else
                {
                    var snapshot = SubscriptionSnapshotStore.TryLoad(input);
                    if (snapshot is not null && snapshot.Count > 0)
                    {
                        _servers.Clear();
                        _servers.AddRange(snapshot);
                        foreach (var cfg in AmneziaWgConfigStore.ListImported())
                            _servers.Add(cfg.ToProfile());
                        ApplyNameOverrides();
                        RefreshServerList();
                        SelectLastServer();
                        if (!quiet)
                            SetStatus($"Не удалось обновить подписку — использую сохранённый список ({_servers.Count}).");
                        return;
                    }

                    throw;
                }
            }

            var routingNotes = await ApplyEmbeddedRoutingAsync(body);
            var (cleanBody, _, bodyTitle) = IncyDeepLink.SplitSubscriptionBody(body);
            if (!string.IsNullOrWhiteSpace(bodyTitle)
                && string.IsNullOrWhiteSpace(_settings.SubscriptionProfileTitle))
                _settings.SubscriptionProfileTitle = bodyTitle;
            body = cleanBody;

            var parsed = SubscriptionParser.Parse(body);
            if (parsed.Count == 0)
            {
                var snapshot = SubscriptionSnapshotStore.TryLoad(input);
                if (snapshot is not null && snapshot.Count > 0)
                {
                    _servers.Clear();
                    _servers.AddRange(snapshot);
                    foreach (var cfg in AmneziaWgConfigStore.ListImported())
                        _servers.Add(cfg.ToProfile());
                    ApplyNameOverrides();
                    RefreshServerList();
                    SelectLastServer();
                    if (!quiet)
                        SetStatus($"Подписка без серверов — использую сохранённый список ({_servers.Count}).");
                    return;
                }

                if (!quiet)
                    SetStatus("Серверы не найдены. Нужен список URI, base64-подписка или .conf AmneziaWG.");
                return;
            }

            foreach (var s in parsed)
                s.IsWhitelistBypass = ServerClassifier.IsWhitelistBypass(s);

            _servers.Clear();
            _servers.AddRange(parsed);
            foreach (var cfg in AmneziaWgConfigStore.ListImported())
                _servers.Add(cfg.ToProfile());
            ApplyNameOverrides();
            RefreshServerList();
            SubscriptionSnapshotStore.Save(input, _servers.Where(s => s.Protocol != "amneziawg"));
            _settings.SubscriptionInput = input;
            PersistSettings();
            if (string.IsNullOrWhiteSpace(_settings.SubscriptionProfileTitle)
                && !string.IsNullOrWhiteSpace(_importedProviderName))
                _settings.SubscriptionProfileTitle = _importedProviderName;
            _importedProviderName = null;
            UpdateSubscriptionInfo();

            if (_servers.Count == 0)
            {
                if (!quiet)
                    SetStatus("Серверы не найдены. Нужен список URI, base64-подписка или .conf AmneziaWG.");
                return;
            }

            SelectLastServer();
            var wl = _servers.Count(s => s.IsWhitelistBypass);
            var awg = _servers.Count(s => s.Protocol == "amneziawg");
            if (announce)
            {
                ServerList.Focus();
                var extra = string.IsNullOrEmpty(routingNotes) ? "" : " " + routingNotes;
                SetStatus(fromCache
                    ? $"Из кэша: {_servers.Count} (обход БС: {wl}, AWG: {awg}).{extra}"
                    : $"Загружено: {_servers.Count} (обход БС: {wl}, AmneziaWG: {awg}).{extra}");
            }
            else if (!quiet)
            {
                var extra = string.IsNullOrEmpty(routingNotes) ? "" : " " + routingNotes;
                SetStatus($"Подписка обновлена: {_servers.Count} (обход БС: {wl}, AWG: {awg}).{extra}");
            }
        }
        catch (Exception ex)
        {
            var input = SubscriptionFetcher.NormalizeInput(_settings.SubscriptionInput ?? "");
            var snapshot = SubscriptionSnapshotStore.TryLoad(input);
            if (snapshot is not null && snapshot.Count > 0)
            {
                _servers.Clear();
                _servers.AddRange(snapshot);
                foreach (var cfg in AmneziaWgConfigStore.ListImported())
                    _servers.Add(cfg.ToProfile());
                ApplyNameOverrides();
                RefreshServerList();
                SelectLastServer();
                if (!quiet)
                    SetStatus($"Ошибка загрузки — использую сохранённый список ({_servers.Count}). {ex.Message}");
                return;
            }

            if (!quiet)
                SetStatus("Ошибка загрузки: " + ex.Message);
            AppLogService.Error("Ошибка загрузки подписки", ex);
        }
    }

    private void UpdateSubscriptionInfo()
    {
        if (SubscriptionInfoText is null)
            return;

        if (SubscriptionRefreshButton is not null)
            SubscriptionRefreshButton.IsEnabled = !string.IsNullOrWhiteSpace(_settings.SubscriptionInput)
                                                 && !AmneziaWgConfigStore.LooksLikeConf(
                                                     _settings.SubscriptionInput);

        var parts = new List<string>();
        var raw = _settings.SubscriptionInput?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            SubscriptionInfoText.Text = "Подписка не задана. Alt, затем П — меню «Подписка».";
            return;
        }

        if (AmneziaWgConfigStore.LooksLikeConf(raw))
        {
            parts.Add("Сохранён текст AmneziaWG-конфига (скрыт)");
        }
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string host;
            try { host = new Uri(raw).Host; }
            catch { host = "ссылка"; }
            parts.Add($"Подписка: {host}");
        }
        else
        {
            var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
            parts.Add(lines > 1
                ? $"Подписка: список ({lines} строк)"
                : "Подписка сохранена");
        }

        if (!string.IsNullOrWhiteSpace(_settings.SubscriptionProfileTitle))
            parts.Insert(0, $"профиль «{_settings.SubscriptionProfileTitle.Trim()}»");

        var updated = _settings.SubscriptionLastUpdateUtc
                      ?? SubscriptionFetcher.TryGetCacheTimestamp(raw);
        if (updated is not null)
        {
            var local = updated.Value.ToLocalTime();
            parts.Add($"обновлена {local:dd.MM.yyyy HH:mm}");
        }

        var used = (_settings.SubscriptionUploadBytes ?? 0) + (_settings.SubscriptionDownloadBytes ?? 0);
        var total = _settings.SubscriptionTotalBytes ?? 0;
        if (used > 0 || total > 0)
        {
            if (total > 0)
            {
                var left = Math.Max(0, total - used);
                parts.Add($"трафик {FormatBytes(used)} / {FormatBytes(total)} (осталось {FormatBytes(left)})");
            }
            else
            {
                parts.Add($"использовано {FormatBytes(used)}");
            }
        }

        if (_settings.SubscriptionExpireUnix is > 0)
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(_settings.SubscriptionExpireUnix.Value).ToLocalTime();
            var left = exp - DateTimeOffset.Now;
            parts.Add(left.TotalSeconds > 0
                ? $"действует до {exp:dd.MM.yyyy HH:mm} (осталось {FormatDuration(left)})"
                : $"срок истёк {exp:dd.MM.yyyy}");
            parts.Add(left.TotalSeconds > 0 ? "состояние активна" : "состояние срок истёк");
        }
        else
        {
            parts.Add("состояние активна");
        }

        if (_servers.Count > 0)
            parts.Add($"серверов {_servers.Count}");
        else
            parts.Add("серверов 0");

        var interval = _settings.SubscriptionProfileUpdateIntervalHours is > 0
            ? $"автообновление каждые {_settings.SubscriptionProfileUpdateIntervalHours} ч"
            : "автообновление вручную или по настройке приложения";
        parts.Add(interval);
        if (!string.IsNullOrWhiteSpace(_settings.SubscriptionSupportUrl))
            parts.Add($"поддержка {_settings.SubscriptionSupportUrl.Trim()}");
        SubscriptionInfoText.Text = string.Join(". ", parts) + ".";
    }

    private async void SubscriptionRefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshSubscriptionAsync();

    private void ResetSubscriptionMetadata()
    {
        _settings.SubscriptionLastUpdateUtc = null;
        _settings.SubscriptionUploadBytes = null;
        _settings.SubscriptionDownloadBytes = null;
        _settings.SubscriptionTotalBytes = null;
        _settings.SubscriptionExpireUnix = null;
        _settings.SubscriptionProfileTitle = null;
        _settings.SubscriptionSupportUrl = null;
        _settings.SubscriptionProfileUpdateIntervalHours = null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} Б";
        double v = bytes;
        string[] units = ["КБ", "МБ", "ГБ", "ТБ"];
        var i = -1;
        do
        {
            v /= 1024;
            i++;
        } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays >= 1)
        {
            var days = (int)t.TotalDays;
            var hours = t.Hours;
            return hours > 0 ? $"{days} дн. {hours} ч" : $"{days} дн.";
        }
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} ч";
        return $"{Math.Max(1, (int)t.TotalMinutes)} мин";
    }

    private async void PingSelectedButton_OnClick(object sender, RoutedEventArgs e) =>
        await PingSelectedServerAsync();

    private async void MenuPingSelected_OnClick(object sender, RoutedEventArgs e) =>
        await PingSelectedServerAsync();

    private async void PingButton_OnClick(object sender, RoutedEventArgs e) =>
        await PingAllServersAsync();

    private async void DiagnosticsButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunSelectedServerDiagnosticsAsync();

    private async Task RunSelectedServerDiagnosticsAsync()
    {
        if (ServerList.SelectedItem is not ServerProfile server)
        {
            SetStatus("Выберите сервер для диагностики.");
            return;
        }

        SetStatus($"Диагностика «{server.Name}»…", important: false);
        try
        {
            var connected = IsVpnConnected
                            && _connectedServer is not null
                            && string.Equals(_connectedServer.RawUri, server.RawUri, StringComparison.Ordinal);
            int? port = connected && !_awgConnected
                ? _activeCore == ProxyCoreKind.Xray ? _xray.ActivePort : _runner.ActiveMixedPort
                : null;
            var coreRunning = connected && !_awgConnected
                && (_activeCore == ProxyCoreKind.Xray ? _xray.IsRunning : _runner.IsRunning);

            var report = await ServerDiagnosticsService.RunAsync(
                server, connected, coreRunning, port);

            SetStatus($"Диагностика «{server.Name}»: {report.BuildSummary(connected)}");
            _tray?.Notify(report.BuildSummary(connected));
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка диагностики: " + ex.Message);
        }
    }

    private async Task PingSelectedServerAsync()
    {
        if (ServerList.SelectedItem is not ServerProfile server)
        {
            SetStatus("Выберите сервер для пинга.");
            return;
        }

        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;
        server.LatencyMs = -1;
        RefreshServerList();
        SetStatus($"Пинг «{server.Name}»…");
        try
        {
            var ms = await PingService.PingAsync(server, ct);
            server.LatencyMs = ms;
            RefreshServerList();

            int? tunnelMs = null;
            if (IsVpnConnected && !_awgConnected
                && _connectedServer is not null
                && string.Equals(_connectedServer.RawUri, server.RawUri, StringComparison.Ordinal))
            {
                var port = _activeCore == ProxyCoreKind.Xray
                    ? _xray.ActivePort
                    : _runner.ActiveMixedPort;
                tunnelMs = await PingService.PingViaTunnelAsync(port, ct);
            }

            SetStatus(ms is null
                ? $"«{server.Name}»: нет ответа (прямой TCP {server.Host}:{server.Port}, 3 с)."
                : tunnelMs is int t
                    ? $"«{server.Name}»: TCP {ms} мс, через туннель {t} мс."
                    : $"«{server.Name}»: TCP {ms} мс (прямое подключение, не через VPN).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Пинг отменён.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка пинга: " + ex.Message);
        }
    }

    private async Task PingAllServersAsync()
    {
        if (_servers.Count == 0)
        {
            SetStatus("Сначала загрузите серверы.");
            return;
        }

        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;

        foreach (var s in _servers)
            s.LatencyMs = -1;
        RefreshServerList();
        var uniqueEndpoints = _servers
            .Select(s => $"{s.Host}|{s.Port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        SetStatus($"Пинг {_servers.Count} серверов ({uniqueEndpoints} уникальных endpoint)…");

        try
        {
            var progress = new Progress<(ServerProfile server, int? ms)>(_ =>
            {
                RefreshServerList();
            });

            await PingService.PingAllAsync(_servers, progress, ct);

            var ok = _servers.Count(s => s.LatencyMs is > 0);
            var best = _servers.Where(s => s.LatencyMs is > 0).OrderBy(s => s.LatencyMs).FirstOrDefault();
            RefreshServerList();

            if (best is not null)
            {
                SetStatus(
                    $"Пинг готов: отвечают {ok} из {_servers.Count} (прямой TCP). " +
                    $"Проверено уникальных endpoint: {uniqueEndpoints}. " +
                    $"Лучший: {best.Name}, TCP {best.LatencyMs} мс.");
                if (ServerList.SelectedItem is null)
                    ServerList.SelectedItem = best;
            }
            else
            {
                SetStatus($"Пинг готов: ни один сервер не ответил по прямому TCP за 3 секунды ({_servers.Count} шт.).");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Пинг отменён.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка пинга: " + ex.Message);
        }
    }

    private async void ConnectToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        await ConnectToggleAsync();

    private async Task ConnectToggleAsync()
    {
        if (IsVpnConnected)
            await DisconnectAsync();
        else
            await ConnectAsync(allowFailover: true);
    }

    private bool IsVpnConnected => _connectedServer is not null || _awgConnected;

    private void UpdateConnectToggleUi()
    {
        if (ConnectToggleButton is null)
            return;
        if (IsVpnConnected)
        {
            ConnectToggleButton.Content = "Отключить (Ctrl+Shift+C)";
            AutomationProperties.SetName(ConnectToggleButton, "Отключить прокси");
            AutomationProperties.SetHelpText(ConnectToggleButton,
                "Сейчас подключено. Нажмите, чтобы отключить. Горячая клавиша Ctrl+Shift+C");
        }
        else
        {
            ConnectToggleButton.Content = "Подключить (Ctrl+Shift+C)";
            AutomationProperties.SetName(ConnectToggleButton, "Подключить выбранный сервер");
            AutomationProperties.SetHelpText(ConnectToggleButton,
                "Подключить выбранный сервер. Горячая клавиша Ctrl+Shift+C");
        }
    }

    private async Task CheckConnectionAsync()
    {
        if (!IsVpnConnected)
        {
            SetStatus("Сейчас не подключено.");
            _tray?.Notify("Не подключено");
            return;
        }

        SetStatus("Проверка соединения…");
        try
        {
            bool ok;
            if (_awgConnected || _connectedServer?.Protocol == "amneziawg")
                ok = await ProbeDirectAsync();
            else if (_activeCore == ProxyCoreKind.Xray)
                ok = await _xray.ProbeConnectivityAsync();
            else
                ok = await _runner.ProbeConnectivityAsync();

            var name = _connectedServer?.Name ?? "туннель";
            if (ok)
            {
                SetStatus($"Соединение в порядке («{name}»).");
                _tray?.Notify($"OK: {name}");
            }
            else
            {
                SetStatus($"Соединение не отвечает («{name}»). Попробуйте другой сервер или переподключение.");
                _tray?.Notify($"Нет ответа: {name}");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка проверки: " + ex.Message);
            _tray?.Notify("Ошибка проверки");
        }
    }

    private void MenuRenameServer_OnClick(object sender, RoutedEventArgs e) => RenameSelectedServer();

    private void MenuResetServerName_OnClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerProfile s)
            return;
        _settings.ServerNameOverrides.Remove(s.RawUri);
        if (!string.IsNullOrWhiteSpace(s.OriginalName))
            s.Name = s.OriginalName;
        PersistSettings();
        RefreshServerList();
        SetStatus($"Имя сброшено: {s.Name}");
    }

    private void RenameSelectedServer()
    {
        if (ServerList.SelectedItem is not ServerProfile s)
        {
            SetStatus("Выберите сервер для переименования.");
            return;
        }

        var name = PromptText("Переименовать сервер", "Новое имя (сохраняется локально):", s.Name);
        if (name is null)
            return;
        name = name.Trim();
        if (name.Length == 0)
        {
            SetStatus("Имя не может быть пустым.");
            return;
        }

        s.OriginalName ??= s.Name;
        s.Name = name;
        _settings.ServerNameOverrides[s.RawUri] = name;
        PersistSettings();
        RefreshServerList();
        SetStatus($"Сервер переименован: {name}");
    }

    private string? PromptText(string title, string label, string initial)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = initial,
            MinWidth = 380,
            MinHeight = 28,
            Margin = new Thickness(0, 8, 0, 12)
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            MinWidth = 90,
            MinHeight = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            MinWidth = 90,
            MinHeight = 28,
            IsCancel = true
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        box.SelectAll();
        box.Focus();
        return dlg.ShowDialog() == true ? result : null;
    }

    private async void MenuImportRoutingProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!System.Windows.Clipboard.ContainsText())
        {
            SetStatus("Буфер обмена пуст. Скопируйте happ://routing/add/… или incy://routing/add/…");
            return;
        }

        await ApplyImportedTextAsync(System.Windows.Clipboard.GetText(), "профиля маршрутизации");
    }

    private async Task ApplyRoutingOffAsync()
    {
        _settings.ActiveRoutingProfileId = null;
        RefreshRoutingProfileBox();
        SelectRoutingProfile(null);
        CaptureSettingsFromUi();
        PersistSettings();
        SetStatus("Маршрутизация INCY отключена. Используется встроенный DNS. Переподключитесь, чтобы применить.");
        await Task.CompletedTask;
    }

    private async Task ImportRoutingPayloadAsync(string payload, bool activate)
    {
        payload = await IncyDeepLink.ResolveRoutingPayloadAsync(payload);
        var profile = HappRoutingImporter.Parse(payload);
        var (saved, added) = HappRoutingProfileStore.Import(profile);
        if (activate)
            _settings.ActiveRoutingProfileId = saved.Id;
        RefreshRoutingProfileBox();
        SelectRoutingProfile(_settings.ActiveRoutingProfileId);
        CaptureSettingsFromUi();
        PersistSettings();
        SetStatus(added
            ? $"Импортирован DNS-профиль «{saved.DisplayName}». Переподключитесь, чтобы применить."
            : $"Обновлён DNS-профиль «{saved.DisplayName}».");
    }

    private async Task<string> ApplyEmbeddedRoutingAsync(string body)
    {
        var notes = new List<string>();
        var pending = _settings.PendingRoutingLink;
        _settings.PendingRoutingLink = null;
        var (_, bodyLinks, _) = IncyDeepLink.SplitSubscriptionBody(body);
        var links = new List<string>();
        if (!string.IsNullOrWhiteSpace(pending))
            links.Add(pending);
        links.AddRange(bodyLinks);

        foreach (var raw in links.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (raw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("://routing/off", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyRoutingOffAsync();
                    notes.Add("Маршрутизация отключена провайдером.");
                    continue;
                }

                if (IncyDeepLink.TryParse(raw.StartsWith("://", StringComparison.Ordinal) ? "incy" + raw : raw, out var incy)
                    && incy.Kind == IncyLinkKind.RoutingOff)
                {
                    await ApplyRoutingOffAsync();
                    notes.Add("Маршрутизация отключена провайдером.");
                    continue;
                }

                if (incy is { Kind: IncyLinkKind.RoutingProfile })
                {
                    await ImportRoutingPayloadAsync(incy.Payload, incy.ActivateRouting);
                    notes.Add("Импортирован профиль маршрутизации INCY.");
                    continue;
                }

                if (HappRoutingImporter.LooksLikeRoutingLink(raw)
                    || HappRoutingImporter.LooksLikeRoutingJson(raw)
                    || raw.StartsWith("://", StringComparison.Ordinal))
                {
                    var payload = raw.StartsWith("://", StringComparison.Ordinal) ? "incy" + raw : raw;
                    if (IncyDeepLink.TryParse(payload, out var parsed)
                        && parsed.Kind == IncyLinkKind.RoutingProfile)
                    {
                        await ImportRoutingPayloadAsync(parsed.Payload, parsed.ActivateRouting);
                    }
                    else
                        await ImportRoutingPayloadAsync(payload, activate: true);
                    notes.Add("Импортирован профиль маршрутизации.");
                }
            }
            catch (Exception ex)
            {
                AppLogService.Error("INCY routing import failed", ex);
                notes.Add("Профиль маршрутизации не импортирован: " + ex.Message);
            }
        }

        return string.Join(" ", notes);
    }

    private void MenuDeleteRoutingProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var id = GetSelectedRoutingProfileId();
        if (string.IsNullOrWhiteSpace(id))
        {
            SetStatus("Выберите импортированный DNS-профиль для удаления.");
            return;
        }

        var profile = _routingProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (profile is null)
        {
            SetStatus("Профиль не найден.");
            return;
        }

        HappRoutingProfileStore.Remove(id);
        if (string.Equals(_settings.ActiveRoutingProfileId, id, StringComparison.Ordinal))
            _settings.ActiveRoutingProfileId = null;
        RefreshRoutingProfileBox();
        SelectRoutingProfile(_settings.ActiveRoutingProfileId);
        CaptureSettingsFromUi();
        PersistSettings();
        SetStatus($"DNS-профиль «{profile.DisplayName}» удалён.");
    }

    private void RefreshRoutingProfileBox()
    {
        if (DnsProfileBox is null)
            return;

        _routingProfiles = HappRoutingProfileStore.LoadAll().ToList();
        var selected = GetSelectedRoutingProfileId();
        _loadingUi = true;
        DnsProfileBox.Items.Clear();
        DnsProfileBox.Items.Add(new ComboBoxItem
        {
            Tag = "",
            Content = "По умолчанию (встроенный DNS)",
            IsSelected = string.IsNullOrWhiteSpace(selected)
        });
        foreach (var profile in _routingProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            DnsProfileBox.Items.Add(new ComboBoxItem
            {
                Tag = profile.Id,
                Content = profile.DisplayName
            });
        }

        SelectRoutingProfile(selected);
        _loadingUi = false;
    }

    private void SelectRoutingProfile(string? id)
    {
        if (DnsProfileBox is null)
            return;

        foreach (var item in DnsProfileBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string ?? "", id ?? "", StringComparison.Ordinal))
            {
                DnsProfileBox.SelectedItem = item;
                return;
            }
        }

        if (DnsProfileBox.Items.Count > 0)
            DnsProfileBox.SelectedIndex = 0;
    }

    private string? GetSelectedRoutingProfileId()
    {
        if (DnsProfileBox?.SelectedItem is ComboBoxItem { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            return tag;
        return null;
    }

    private void DnsProfileBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;

        _settings.ActiveRoutingProfileId = GetSelectedRoutingProfileId();
        CaptureSettingsFromUi();
        PersistSettings();

        var profile = HappRoutingProfileStore.FindById(_settings.ActiveRoutingProfileId);
        if (profile?.FakeDns == true && TunCheck?.IsChecked != true)
        {
            SetStatus(
                $"Выбран «{profile.DisplayName}». FakeDNS работает с TUN — включите TUN и переподключитесь.");
            return;
        }

        if (profile is not null)
            SetStatus($"Выбран DNS-профиль «{profile.DisplayName}». Переподключитесь для применения.");
    }

    private async void MenuImportDomainListUrl_OnClick(object sender, RoutedEventArgs e)
    {
        var url = PromptText(
            "Импорт списка доменов",
            "URL сырого списка (GitHub raw, geosite-строки или домены по одному в строке):",
            "https://raw.githubusercontent.com/");
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var body = await client.GetStringAsync(url.Trim());
            var lines = body.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#') && l.Length > 0)
                .Take(5000)
                .ToList();
            if (lines.Count == 0)
            {
                SetStatus("Список пуст или не распознан.");
                return;
            }

            var existing = DomainListBox.Text ?? "";
            var merged = string.IsNullOrWhiteSpace(existing)
                ? string.Join(Environment.NewLine, lines)
                : existing.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, lines);
            DomainListBox.Text = merged;
            CaptureSettingsFromUi();
            PersistSettings();
            SetStatus($"Импортировано строк: {lines.Count}. Сохраните и выберите режим «Только сайты…» или «кроме сайтов…».");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось загрузить список: " + ex.Message);
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        CaptureSettingsFromUi();
        PersistSettings();
        RestartBackgroundTimers();
        UpdateSubscriptionInfo();
        SetStatus("Настройки сохранены.");
    }

    private async void ServerList_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            RenameSelectedServer();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConnectToggleAsync();
        }
    }

    private void ServerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || ServerList.SelectedItem is not ServerProfile s)
            return;
        _settings.LastServerUri = s.RawUri;
        _settings.LastServerName = s.Name;
        PersistSettings();
    }

    private void SettingsControl_Changed(object sender, EventArgs e)
    {
        if (_loadingUi)
            return;
        var wasKillSwitchEnabled = _settings.KillSwitchEnabled;
        var oldFingerprint = _activeConnectionFingerprint;
        CaptureSettingsFromUi();
        PersistSettings();
        RestartBackgroundTimers();

        if (IsVpnConnected && wasKillSwitchEnabled != _settings.KillSwitchEnabled)
        {
            if (_settings.KillSwitchEnabled)
                _killSwitch.Arm(GetActiveCoreExePath());
            else
                _killSwitch.Disarm();
        }

        var newFingerprint = BuildConnectionFingerprint();
        if (IsVpnConnected
            && !_settingsReconnectBusy
            && !string.IsNullOrEmpty(oldFingerprint)
            && !string.Equals(oldFingerprint, newFingerprint, StringComparison.Ordinal))
        {
            _ = ReconnectAfterSettingsChangeAsync();
        }
    }

    private string BuildConnectionFingerprint() =>
        string.Join("|",
            TunCheck.IsChecked == true,
            SystemProxyCheck.IsChecked == true,
            GetSelectedTunStack(),
            EngineOptions.ClampPort(int.TryParse(MixedPortBox.Text.Trim(), out var port)
                ? port
                : EngineOptions.DefaultMixedPort),
            GetSelectedProxyCoreSetting(),
            GetSelectedRoutingModeTag(),
            DomainListBox.Text?.Trim() ?? "",
            AppListBox.Text?.Trim() ?? "");

    private async Task ReconnectAfterSettingsChangeAsync()
    {
        if (_settingsReconnectBusy || _connectedServer is null)
            return;

        _settingsReconnectBusy = true;
        try
        {
            var server = _connectedServer;
            SetStatus("Настройки подключения изменились — переподключаюсь…");
            await ConnectAsync(allowFailover: false, server);
        }
        finally
        {
            _settingsReconnectBusy = false;
        }
    }

    private void TunCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        var oldFingerprint = _activeConnectionFingerprint;

        if (TunCheck.IsChecked == true)
        {
            if (SystemProxyCheck.IsChecked == true)
            {
                _loadingUi = true;
                SystemProxyCheck.IsChecked = false;
                _loadingUi = false;
            }

            if (!ElevationHelper.IsElevated)
            {
                CaptureSettingsFromUi();
                _settings.UseTun = true;
                PersistSettings();
                SetStatus("Перезапуск от имени администратора для режима TUN…");
                if (ElevationHelper.TryRelaunchElevated())
                {
                    _exitRequested = true;
                    Cleanup();
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                _loadingUi = true;
                TunCheck.IsChecked = false;
                _loadingUi = false;
                _settings.UseTun = false;
                PersistSettings();
                SetStatus("TUN требует права администратора. Запрос UAC отклонён.");
                return;
            }

            Title = "Happ Accessible (администратор)";
        }

        CaptureSettingsFromUi();
        PersistSettings();
        RestartBackgroundTimers();
        if (IsVpnConnected
            && !_settingsReconnectBusy
            && !string.IsNullOrEmpty(oldFingerprint)
            && !string.Equals(oldFingerprint, BuildConnectionFingerprint(), StringComparison.Ordinal))
            _ = ReconnectAfterSettingsChangeAsync();
    }

    private TrayMenuSnapshot BuildTraySnapshot()
    {
        // WinForms Opening can call this off the WPF UI thread.
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(BuildTraySnapshot);

        var selected = ServerList?.SelectedItem as ServerProfile;
        return new TrayMenuSnapshot
        {
            IsConnected = _connectedServer is not null || _awgConnected,
            ConnectedUri = _connectedServer?.RawUri,
            SelectedUri = selected?.RawUri ?? _settings.LastServerUri,
            Servers = _servers.Select(s => new TrayServerEntry(
                s.RawUri,
                s.Name,
                s.Protocol,
                s.IsWhitelistBypass,
                s.LatencyMs)).ToList()
        };
    }

    private async Task ConnectServerFromTrayAsync(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        var server = _servers.FirstOrDefault(s =>
            string.Equals(s.RawUri, uri, StringComparison.Ordinal));
        if (server is null)
        {
            SetStatus("Сервер из меню трея больше не в списке. Обновите подписку.");
            return;
        }

        ServerList.SelectedItem = server;
        _settings.LastServerUri = server.RawUri;
        _settings.LastServerName = server.Name;
        PersistSettings();

        if (_connectedServer is not null
            && string.Equals(_connectedServer.RawUri, server.RawUri, StringComparison.Ordinal))
        {
            SetStatus($"Уже подключено: {server.Name}.");
            return;
        }

        await ConnectAsync(allowFailover: true, server);
    }

    private async Task ConnectAsync(bool allowFailover = true, ServerProfile? prefer = null)
    {
        var server = prefer ?? ServerList.SelectedItem as ServerProfile;
        if (server is null)
        {
            SetStatus("Сначала выберите сервер в списке или в меню трея.");
            return;
        }

        if (_connectBusy || _failoverBusy)
        {
            SetStatus("Подключение уже выполняется…");
            return;
        }

        if (!ReferenceEquals(ServerList.SelectedItem, server))
            ServerList.SelectedItem = server;

        var outcome = await TryConnectServerAsync(server);
        if (outcome is ConnectOutcome.Success or ConnectOutcome.Busy or ConnectOutcome.Cancelled)
            return;
        if (!allowFailover || !_settings.AutoWhitelistFailover)
            return;

        SetStatus($"«{server.Name}» не дал трафик. Пробую серверы обхода белых списков…");
        await FailoverToWhitelistAsync(excludeUri: server.RawUri);
    }

    private async Task<ConnectOutcome> TryConnectServerAsync(ServerProfile server)
    {
        if (_connectBusy || _coreUpdateBusy)
            return ConnectOutcome.Busy;

        _connectBusy = true;
        var epoch = _sessionEpoch;
        var killSwitchWasArmed = _killSwitch.IsArmed;
        var killSwitchCorePath = GetActiveCoreExePath();
        try
        {
            // A recovery must be able to start the core. The firewall rules are
            // re-armed by RecordConnectSuccess after the new tunnel is ready.
            if (_killSwitch.IsArmed)
                _killSwitch.Disarm();

            if (string.Equals(server.Protocol, "amneziawg", StringComparison.OrdinalIgnoreCase))
                return await TryConnectAmneziaAsync(server, epoch);

            // Leaving AWG if switching to proxy core
            if (_awgConnected || _awg.IsTunnelRunning)
            {
                await _awg.DisconnectAsync();
                _awgConnected = false;
            }

            await _xray.StopAsync();

            var useTun = TunCheck.IsChecked == true;
            var useProxy = SystemProxyCheck.IsChecked == true;
            var routing = GetRoutingOptions();
            var appMode = routing.Mode is RoutingMode.AppProxy or RoutingMode.AppBypass;
            var pref = ProxyCoreKindParser.Parse(_settings.ProxyCore);
            var core = CoreSelector.Resolve(server, pref, useTun || appMode, routing.Mode);

            if (pref == ProxyCoreKind.Xray && core == ProxyCoreKind.SingBox
                && (useTun || appMode || routing.Mode is not RoutingMode.Global))
            {
                SetStatus("Для TUN/маршрутов нужен sing-box — переключаю ядро.");
            }

            if (core == ProxyCoreKind.Xray)
                return await TryConnectXrayAsync(server, epoch, useProxy);

            // —— sing-box path ——
            if (appMode)
            {
                if (routing.Processes.Count == 0)
                {
                    SetStatus("Укажите приложения (chrome.exe и т.п.) для режима по приложениям.");
                    return ConnectOutcome.Failed;
                }

                useTun = true;
                useProxy = false;
                if (TunCheck.IsChecked != true)
                {
                    _loadingUi = true;
                    TunCheck.IsChecked = true;
                    _loadingUi = false;
                }

                if (!ElevationHelper.IsElevated)
                {
                    CaptureSettingsFromUi();
                    _settings.UseTun = true;
                    PersistSettings();
                    SetStatus("Режим по приложениям: перезапуск от администратора…");
                    if (ElevationHelper.TryRelaunchElevated())
                    {
                        _exitRequested = true;
                        Cleanup(persistFromUi: false);
                        System.Windows.Application.Current.Shutdown();
                        return ConnectOutcome.Cancelled;
                    }

                    SetStatus("Режим по приложениям требует TUN от администратора. UAC отклонён.");
                    return ConnectOutcome.Failed;
                }
            }

            if (useTun && !ElevationHelper.IsElevated)
            {
                CaptureSettingsFromUi();
                _settings.UseTun = true;
                PersistSettings();
                SetStatus("Для TUN нужен перезапуск от администратора…");
                if (ElevationHelper.TryRelaunchElevated())
                {
                    _exitRequested = true;
                    Cleanup();
                    System.Windows.Application.Current.Shutdown();
                    return ConnectOutcome.Cancelled;
                }

                SetStatus("TUN требует права администратора. UAC отклонён — подключаю без TUN.");
                useTun = false;
                _loadingUi = true;
                TunCheck.IsChecked = false;
                _loadingUi = false;
            }

            if (useTun && useProxy)
            {
                useProxy = false;
                SetStatus("TUN включён: системный прокси временно не используем (чтобы не ломать сеть).");
            }

            _proxy.DisableIfOwned();

            SetStatus($"Проверка TCP «{server.Name}»…");
            var (tcpOk, tcpDetail) = await ConnectivityProbe.PreflightTcpAsync(
                server, TimeSpan.FromSeconds(3));
            if (!tcpOk)
            {
                _connectedServer = null;
                SetStatus(
                    $"Сервер «{server.Name}» недоступен до запуска ядра ({tcpDetail}). " +
                    "Выберите другой или обновите подписку.");
                _tray?.SetTooltip("Happ Accessible — сервер недоступен");
                UpdateConnectToggleUi();
                return ConnectOutcome.Failed;
            }

            SetStatus(useTun
                ? $"Запуск TUN ({server.Name}, TCP {tcpDetail})…"
                : $"Подключение к «{server.Name}» (TCP {tcpDetail})…");

            await _runner.StartAsync(server, useTun, routing, GetEngineOptions());

            if (epoch != _sessionEpoch)
            {
                await _runner.StopAsync();
                return ConnectOutcome.Cancelled;
            }

            var ok = routing.Mode is RoutingMode.ProxyList or RoutingMode.AppProxy or RoutingMode.AppBypass
                     || await _runner.ProbeConnectivityAsync();
            if (!ok)
            {
                await _runner.StopAsync();
                if (useTun && CoreSelector.LooksRealityOrVision(server.RawUri ?? ""))
                {
                    SetStatus(
                        "TUN + sing-box не ответил — пробую Xray через системный прокси (без TUN)…");
                    var xrayOutcome = await TryConnectXrayAsync(server, epoch, useProxy: true);
                    if (xrayOutcome == ConnectOutcome.Success)
                        _tray?.Notify("Reality/Vision: подключено через Xray без TUN (стабильнее для этого узла).");
                    return xrayOutcome;
                }

                _connectedServer = null;
                var failHint = BuildCoreHint(server, failed: true, tunActive: useTun);
                SetStatus(
                    $"Сервер «{server.Name}» не отвечает через туннель (ядро: sing-box). {failHint}" +
                    "Лог: " + TruncateStatus(FilterLogForStatus(_runner.RecentLog)));
                _tray?.SetTooltip("Happ Accessible — нет связи с сервером");
                return ConnectOutcome.Failed;
            }

            if (epoch != _sessionEpoch)
            {
                _proxy.DisableIfOwned();
                await _runner.StopAsync();
                return ConnectOutcome.Cancelled;
            }

            var engine = GetEngineOptions();
            if (useProxy)
                _proxy.Enable("127.0.0.1", engine.MixedPort);

            _connectedServer = server;
            _activeCore = ProxyCoreKind.SingBox;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();
            RecordConnectSuccess(server);

            var mode = useTun
                ? $"TUN/{engine.TunStack}"
                : useProxy
                    ? "системный прокси"
                    : $"только локальный mixed (127.0.0.1:{engine.MixedPort})";
            var routeLabel = GetRoutingModeLabel();
            var wl = server.IsWhitelistBypass ? " Обход белых списков." : "";
            var coreLabel = string.IsNullOrWhiteSpace(_runner.CoreVersion)
                ? "sing-box"
                : _runner.CoreVersion;
            var hint = BuildCoreHint(server, failed: false);

            var msg =
                $"Подключено: {server.Name}. Ядро: {coreLabel}. {mode}. " +
                $"Маршрут: {routeLabel}.{wl}{hint} Связь проверена.";
            SetStatus(msg);
            _tray?.SetTooltip($"Happ Accessible — {server.Name}");
            _tray?.Notify(msg);
            UpdateConnectToggleUi();
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _proxy.DisableIfOwned();
            await _runner.StopAsync();
            await _xray.StopAsync();
            _connectedServer = null;
            _awgConnected = false;
            if (!string.IsNullOrWhiteSpace(_runner.RecentLog))
                AppLogService.Error("sing-box log: " + _runner.RecentLog);
            AppLogService.Error("Ошибка подключения", ex);
            SetStatus("Ошибка подключения: " + ex.Message);
            _tray?.SetTooltip("Happ Accessible — ошибка");
            UpdateConnectToggleUi();
            return ConnectOutcome.Failed;
        }
        finally
        {
            if (killSwitchWasArmed && !IsVpnConnected)
                _killSwitch.Arm(killSwitchCorePath);
            _connectBusy = false;
        }
    }

    private async Task<ConnectOutcome> TryConnectXrayAsync(ServerProfile server, int epoch, bool useProxy)
    {
        try
        {
            _proxy.DisableIfOwned();
            await _runner.StopAsync();

            var engine = GetEngineOptions();
            SetStatus($"Xray: проверка TCP «{server.Name}»…");
            var (tcpOk, tcpDetail) = await ConnectivityProbe.PreflightTcpAsync(
                server, TimeSpan.FromSeconds(3));
            if (!tcpOk)
            {
                _connectedServer = null;
                SetStatus(
                    $"Сервер «{server.Name}» недоступен ({tcpDetail}). Выберите другой.");
                UpdateConnectToggleUi();
                return ConnectOutcome.Failed;
            }

            SetStatus($"Xray: «{server.Name}» (TCP {tcpDetail})…");
            await _xray.StartAsync(server, engine);

            if (epoch != _sessionEpoch)
            {
                await _xray.StopAsync();
                return ConnectOutcome.Cancelled;
            }

            var ok = await _xray.ProbeConnectivityAsync();
            if (!ok)
            {
                await _xray.StopAsync();
                _connectedServer = null;
                var failHint = BuildCoreHint(server, failed: true);
                SetStatus(
                    $"Сервер «{server.Name}» не отвечает через Xray. {failHint}" +
                    "Лог: " + TruncateStatus(_xray.RecentLog));
                return ConnectOutcome.Failed;
            }

            if (epoch != _sessionEpoch)
            {
                _proxy.DisableIfOwned();
                await _xray.StopAsync();
                return ConnectOutcome.Cancelled;
            }

            if (useProxy)
                _proxy.Enable("127.0.0.1", engine.MixedPort);

            _connectedServer = server;
            _activeCore = ProxyCoreKind.Xray;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();
            RecordConnectSuccess(server);

            var core = string.IsNullOrWhiteSpace(_xray.CoreVersion) ? "Xray" : $"Xray {_xray.CoreVersion}";
            var mode = useProxy
                ? "системный прокси"
                : $"локальный HTTP 127.0.0.1:{engine.MixedPort}";
            var hint = BuildCoreHint(server, failed: false);
            var msg = $"Подключено: {server.Name}. Ядро: {core}. {mode}.{hint} Связь проверена.";
            SetStatus(msg);
            _tray?.SetTooltip($"Happ Accessible — {server.Name} (Xray)");
            _tray?.Notify(msg);
            UpdateConnectToggleUi();
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _proxy.DisableIfOwned();
            await _xray.StopAsync();
            _connectedServer = null;
            if (!string.IsNullOrWhiteSpace(_xray.RecentLog))
                AppLogService.Error("xray log: " + _xray.RecentLog);
            AppLogService.Error("Ошибка Xray", ex);
            SetStatus("Ошибка Xray: " + ex.Message);
            UpdateConnectToggleUi();
            return ConnectOutcome.Failed;
        }
    }

    private async Task<ConnectOutcome> TryConnectAmneziaAsync(ServerProfile server, int epoch)
    {
        var path = AmneziaWgConfigStore.TryResolveSourcePath(server);
        if (path is null)
        {
            SetStatus("Файл AmneziaWG-конфига не найден. Импортируйте .conf снова.");
            return ConnectOutcome.Failed;
        }

        try
        {
            _proxy.DisableIfOwned();
            await _runner.StopAsync();
            await _xray.StopAsync();

            if (epoch != _sessionEpoch)
                return ConnectOutcome.Cancelled;

            SetStatus($"AmneziaWG: «{server.Name}»… подготовка…");
            var progress = new Progress<string>(s => SetStatus(s));
            await _awg.ConnectAsync(path, progress);

            if (epoch != _sessionEpoch)
            {
                await _awg.DisconnectAsync();
                _awgConnected = false;
                return ConnectOutcome.Cancelled;
            }

            var (probeOk, handshakeOk) = await WaitForAwgReadyAsync();

            if (epoch != _sessionEpoch)
            {
                await _awg.DisconnectAsync();
                _awgConnected = false;
                return ConnectOutcome.Cancelled;
            }

            // Service up: keep connected even if HTTP probe is inconclusive
            if (!_awg.IsTunnelRunning)
            {
                await _awg.DisconnectAsync();
                _awgConnected = false;
                _connectedServer = null;
                SetStatus($"AmneziaWG «{server.Name}»: служба туннеля не работает. Проверьте конфиг.");
                return ConnectOutcome.Failed;
            }

            _awgConnected = true;
            _connectedServer = server;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();
            RecordConnectSuccess(server);

            string msg;
            if (probeOk)
                msg = $"Подключено AmneziaWG: {server.Name}. Связь проверена.";
            else if (handshakeOk)
                msg = $"Подключено AmneziaWG: {server.Name}. Рукопожатие есть, внешняя проверка не прошла — откройте сайт вручную.";
            else
                msg = $"Подключено AmneziaWG: {server.Name}. Туннель поднят, но рукопожатие/интернет пока не подтверждены — проверьте сайт или конфиг.";

            SetStatus(msg);
            _tray?.SetTooltip($"Happ Accessible — AWG {server.Name}");
            _tray?.Notify(msg);
            UpdateConnectToggleUi();
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _awgConnected = false;
            _connectedServer = null;
            try { await _awg.DisconnectAsync(); } catch { /* ignore */ }
            AppLogService.Error("Ошибка AmneziaWG", ex);
            SetStatus("Ошибка AmneziaWG: " + ex.Message);
            _tray?.SetTooltip("Happ Accessible — ошибка AWG");
            UpdateConnectToggleUi();
            return ConnectOutcome.Failed;
        }
    }

    private async Task FailoverToWhitelistAsync(string? excludeUri)
    {
        if (_failoverBusy || !_settings.AutoWhitelistFailover)
            return;

        _failoverBusy = true;
        var epoch = _sessionEpoch;
        try
        {
            var whitelist = ServerClassifier.WhitelistOnly(_servers)
                .Where(s => !string.Equals(s.RawUri, excludeUri, StringComparison.Ordinal)
                            && s.Protocol != "amneziawg")
                .ToList();

            var candidates = whitelist.Count > 0
                ? whitelist
                : _servers.Where(s => !string.Equals(s.RawUri, excludeUri, StringComparison.Ordinal)
                                      && s.Protocol != "amneziawg").ToList();

            if (candidates.Count == 0)
            {
                SetStatus("Нет серверов для автопереключения.");
                return;
            }

            // Fast TCP ping for candidates that have no latency yet
            var needPing = candidates.Where(s => s.LatencyMs is null or < 0).Take(12).ToList();
            if (needPing.Count > 0)
            {
                SetStatus($"Пинг кандидатов обхода БС ({needPing.Count})…");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await PingService.PingAllAsync(needPing, null, cts.Token);
                    RefreshServerList();
                }
                catch
                {
                    // continue with whatever we have
                }
            }

            if (epoch != _sessionEpoch)
                return;

            var ordered = ServerClassifier.PreferWhitelistBypass(candidates).Take(8).ToList();
            foreach (var c in ordered)
            {
                if (epoch != _sessionEpoch)
                    return;

                SetStatus($"Пробую обход БС: {c.Name}…");
                var outcome = await TryConnectServerAsync(c);
                if (outcome is ConnectOutcome.Busy or ConnectOutcome.Cancelled)
                    return;
                if (outcome == ConnectOutcome.Success)
                {
                    var kind = c.IsWhitelistBypass ? "обход белых списков" : "запасной сервер";
                    SetStatus($"Автопереключение ({kind}): {c.Name}.");
                    _tray?.Notify($"Переключено на {c.Name}");
                    return;
                }
            }

            if (epoch != _sessionEpoch)
                return;

            SetStatus("Автопереключение не удалось: ни один кандидат обхода БС не дал трафик. Отключаюсь.");
            _tray?.SetTooltip("Happ Accessible — нет связи");
            _tray?.Notify("Нет связи — отключено");
            if (IsVpnConnected)
                await DisconnectAsync(manual: false);
        }
        finally
        {
            _failoverBusy = false;
        }
    }

    private static string TruncateStatus(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s[^160..];
    }

    private async Task DisconnectAsync(bool manual = true)
    {
        _stoppingCores = true;
        try
        {
            _sessionEpoch++;
            _healthMonitor.ResetFailStreak();
            _sessionConnectedUtc = default;
            if (manual && _settings.KillSwitchEnabled)
                _killSwitch.Disarm();
            _manualDisconnect = manual;

            try { _proxy.DisableIfOwned(); }
            catch (Exception ex) { AppLogService.Error("Отключение системного прокси", ex); }
            try { await _runner.StopAsync(); }
            catch (Exception ex) { AppLogService.Error("Остановка sing-box", ex); }
            try { await _xray.StopAsync(); }
            catch (Exception ex) { AppLogService.Error("Остановка Xray", ex); }
            if (_awgConnected || _awg.IsTunnelRunning)
            {
                try { await _awg.DisconnectAsync(); }
                catch (Exception ex) { AppLogService.Warn("AWG disconnect: " + ex.Message); }
            }
            _awgConnected = false;
            _connectedServer = null;
            _activeCore = ProxyCoreKind.SingBox;
            SessionJournalService.Record(manual ? "Отключено вручную." : "Отключено (авто).");
            SetStatus("Отключено.");
            UpdateConnectToggleUi();
            _tray?.SetTooltip("Happ Accessible — не подключено");
            _tray?.Notify("Отключено.");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Ошибка отключения", ex);
            SetStatus("Ошибка отключения: " + ex.Message);
            _connectedServer = null;
            _awgConnected = false;
            UpdateConnectToggleUi();
        }
        finally
        {
            _stoppingCores = false;
        }
    }

    private void RecordConnectSuccess(ServerProfile server)
    {
        _sessionConnectedUtc = DateTime.UtcNow;
        _healthMonitor.ResetFailStreak();
        _manualDisconnect = false;
        _activeConnectionFingerprint = BuildConnectionFingerprint();

        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _settings.ServerLastSuccessUtc[server.RawUri] = unix;
        server.LastSuccessUtc = DateTimeOffset.FromUnixTimeSeconds(unix);

        SessionJournalService.Record($"Подключено: {server.Name}.");
        ApplyNameOverrides();
        RefreshServerList();

        if (_settings.KillSwitchEnabled && ElevationHelper.IsElevated)
            _killSwitch.Arm(GetActiveCoreExePath());

        MaybeShowDoHHint();
    }

    private string? GetActiveCoreExePath()
    {
        if (_awgConnected)
            return _awg.ExePath;
        if (_activeCore == ProxyCoreKind.Xray && File.Exists(_xray.ExePath))
            return _xray.ExePath;

        var sb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "tools", "sing-box.exe");
        return File.Exists(sb) ? sb : null;
    }

    private void MaybeShowDoHHint()
    {
        if (_settings.DoHHintShown || SystemProxyCheck.IsChecked != true)
            return;

        _settings.DoHHintShown = true;
        PersistSettings();
        System.Windows.MessageBox.Show(
            this,
            "При системном прокси Chrome может игнорировать прокси из‑за «Безопасного DNS».\n\n" +
            "Отключите Secure DNS в chrome://settings/security или используйте TUN.",
            "Подсказка: DNS в Chrome",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ServerContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (MenuToggleFavorite is null)
            return;

        if (ServerList.SelectedItem is ServerProfile s && s.IsFavorite)
            MenuToggleFavorite.Header = "Убрать из избранного";
        else
            MenuToggleFavorite.Header = "В избранное";
    }

    private void MenuToggleFavorite_OnClick(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerProfile s)
            return;

        _settings.FavoriteServerUris ??= new HashSet<string>(StringComparer.Ordinal);
        if (_settings.FavoriteServerUris.Contains(s.RawUri))
            _settings.FavoriteServerUris.Remove(s.RawUri);
        else
            _settings.FavoriteServerUris.Add(s.RawUri);

        ApplyNameOverrides();
        RefreshServerList();
        PersistSettings();
        SetStatus(_settings.FavoriteServerUris.Contains(s.RawUri)
            ? $"«{s.Name}» добавлен в избранное."
            : $"«{s.Name}» убран из избранного.");
    }

    private void FavoritesOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;

        _showFavoritesOnly = FavoritesOnlyCheck?.IsChecked == true;
        RefreshServerList();
    }

    private void CaptureSettingsFromUi()
    {
        if (SystemProxyCheck is null || TunCheck is null
            || AutoConnectCheck is null || StartMinimizedCheck is null || ServerList is null
            || RoutingModeBox is null || DomainListBox is null || AppListBox is null
            || AutoUpdateSubCheck is null || AutoWhitelistCheck is null
            || TunStackBox is null || MixedPortBox is null
            || ProxyCoreBox is null || AutoUpdateCoresCheck is null || AutoUpdateAppCheck is null)
            return;

        // Subscription text lives in settings (edited via menu), not a permanent textbox
        _settings.UseSystemProxy = SystemProxyCheck.IsChecked == true;
        _settings.UseTun = TunCheck.IsChecked == true;
        _settings.TunStack = GetSelectedTunStack();
        _settings.MixedPort = EngineOptions.ClampPort(
            int.TryParse(MixedPortBox?.Text?.Trim(), out var p) ? p : EngineOptions.DefaultMixedPort);
        if (MixedPortBox is not null)
            MixedPortBox.Text = _settings.MixedPort.ToString();
        _settings.ProxyCore = GetSelectedProxyCoreSetting();
        _settings.AutoUpdateCores = AutoUpdateCoresCheck.IsChecked == true;
        _settings.AutoUpdateApp = AutoUpdateAppCheck.IsChecked == true;
        if (KillSwitchCheck is not null)
            _settings.KillSwitchEnabled = KillSwitchCheck.IsChecked == true;
        _settings.AutoConnect = AutoConnectCheck.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCheck.IsChecked == true;
        _settings.AutoUpdateSubscription = AutoUpdateSubCheck.IsChecked == true;
        _settings.AutoWhitelistFailover = AutoWhitelistCheck.IsChecked == true;
        _settings.RoutingMode = GetSelectedRoutingModeTag();
        _settings.DomainList = DomainListBox.Text ?? "";
        _settings.AppList = AppListBox.Text ?? "";
        _settings.ActiveRoutingProfileId = GetSelectedRoutingProfileId();
        if (ServerList.SelectedItem is ServerProfile s)
        {
            _settings.LastServerUri = s.RawUri;
            _settings.LastServerName = s.Name;
        }
    }

    private void RoutingModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        UpdateDomainListVisibility();
        EnsureTunForAppRouting();
        CaptureSettingsFromUi();
        PersistSettings();
    }

    private void EnsureTunForAppRouting()
    {
        var tag = GetSelectedRoutingModeTag();
        if (tag is not ("app-proxy" or "app-bypass"))
            return;

        if (SystemProxyCheck.IsChecked == true)
        {
            _loadingUi = true;
            SystemProxyCheck.IsChecked = false;
            _loadingUi = false;
        }

        if (TunCheck.IsChecked != true)
        {
            SetStatus("Режим по приложениям требует TUN — включаю…");
            TunCheck.IsChecked = true; // may trigger UAC relaunch
        }
    }

    private void UpdateDomainListVisibility()
    {
        if (DomainListBox is null || DomainListLabel is null || AppListBox is null || AppListLabel is null)
            return;

        var tag = GetSelectedRoutingModeTag();
        var needDomains = tag is "proxy-list" or "bypass-list" or "bypass-ru";
        var needApps = tag is "app-proxy" or "app-bypass";

        DomainListBox.IsEnabled = needDomains;
        DomainListLabel.IsEnabled = needDomains;
        DomainListBox.Opacity = needDomains ? 1 : 0.5;
        DomainListBox.Visibility = needApps ? Visibility.Collapsed : Visibility.Visible;
        DomainListLabel.Visibility = needApps ? Visibility.Collapsed : Visibility.Visible;

        AppListBox.IsEnabled = needApps;
        AppListLabel.IsEnabled = needApps;
        AppListBox.Opacity = needApps ? 1 : 0.5;
        AppListBox.Visibility = needApps ? Visibility.Visible : Visibility.Collapsed;
        AppListLabel.Visibility = needApps ? Visibility.Visible : Visibility.Collapsed;

        if (DomainListLabel is not null && tag == "bypass-ru")
            DomainListLabel.Content = "Дополнительно без VPN (по желанию), например mybank.ru";
        else if (DomainListLabel is not null)
            DomainListLabel.Content = "Список сайтов (по одному в строке), например youtube.com";

        if (AppListLabel is not null && tag == "app-proxy")
            AppListLabel.Content = "Через VPN (по одному в строке), например chrome.exe";
        else if (AppListLabel is not null)
            AppListLabel.Content = "Без VPN (по одному в строке), например chrome.exe";
    }

    private void SelectRoutingMode(string? mode)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? "global" : mode.Trim().ToLowerInvariant();
        foreach (var item in RoutingModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                RoutingModeBox.SelectedItem = item;
                return;
            }
        }

        RoutingModeBox.SelectedIndex = 0;
    }

    private string GetSelectedRoutingModeTag()
    {
        if (RoutingModeBox.SelectedItem is ComboBoxItem { Tag: string tag })
            return tag;
        return "global";
    }

    private string GetRoutingModeLabel() => GetSelectedRoutingModeTag() switch
    {
        "proxy-list" => "только сайты из списка",
        "bypass-list" => "всё, кроме списка",
        "bypass-ru" => "всё, кроме РФ",
        "app-proxy" => "только выбранные приложения",
        "app-bypass" => "всё, кроме выбранных приложений",
        _ => "всё через VPN"
    };

    private EngineOptions GetEngineOptions()
    {
        CaptureSettingsFromUi();
        var engine = new EngineOptions
        {
            MixedPort = EngineOptions.ClampPort(_settings.MixedPort),
            TunStack = EngineOptions.NormalizeTunStack(_settings.TunStack),
            DnsStrategy = _settings.DnsStrategy,
            DnsRemoteServer = _settings.DnsRemoteServer,
            DnsRemoteFallback = _settings.DnsRemoteFallback,
            DnsRemoteType = _settings.DnsRemoteType,
            DnsRemoteDomain = _settings.DnsRemoteDomain,
            DnsDomesticServer = _settings.DnsDomesticServer,
            DnsDomesticType = _settings.DnsDomesticType,
            DnsDomesticDomain = _settings.DnsDomesticDomain,
            FakeDns = _settings.FakeDns,
            RejectQuicUdp443 = _settings.RejectQuicUdp443
        };

        var profile = HappRoutingProfileStore.FindById(_settings.ActiveRoutingProfileId);
        return profile is null ? engine : EngineOptions.FromProfile(profile, engine);
    }

    private void SelectTunStack(string? stack)
    {
        stack = EngineOptions.NormalizeTunStack(stack);
        if (TunStackBox is null)
            return;
        foreach (var item in TunStackBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, stack, StringComparison.OrdinalIgnoreCase))
            {
                TunStackBox.SelectedItem = item;
                return;
            }
        }

        TunStackBox.SelectedIndex = 0;
    }

    private string GetSelectedTunStack()
    {
        if (TunStackBox?.SelectedItem is ComboBoxItem { Tag: string tag })
            return EngineOptions.NormalizeTunStack(tag);
        return EngineOptions.DefaultTunStack;
    }

    /// <summary>Hints for Reality/Vision/xhttp when core choice matters.</summary>
    private static string BuildCoreHint(ServerProfile server, bool failed, bool tunActive = false)
    {
        var uri = server.RawUri ?? "";
        if (CoreSelector.NeedsXrayTransport(uri))
        {
            return failed
                ? " Узел xhttp: нужен sing-box-lx (автообновление ядер) или Xray без TUN. "
                : " (xhttp: sing-box-lx / Xray.)";
        }

        var reality = uri.Contains("security=reality", StringComparison.OrdinalIgnoreCase)
                      || uri.Contains("pbk=", StringComparison.OrdinalIgnoreCase);
        var vision = uri.Contains("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase)
                     || uri.Contains("flow=xtls", StringComparison.OrdinalIgnoreCase);

        if (!reality && !vision)
            return "";

        if (failed)
        {
            if (tunActive)
            {
                return " Узел Reality/Vision в TUN: sing-box может подниматься дольше — " +
                       "попробуйте ещё раз, отключите TUN (ядро Xray), смените TUN stack или порт mixed. ";
            }

            return " Узел Reality/Vision: попробуйте ядро Xray (Авто/Xray), другой сервер, " +
                   "или смените TUN stack / порт mixed. ";
        }

        return " (Reality/Vision: в Авто обычно Xray.)";
    }

    private static string FilterLogForStatus(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return "—";

        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var important = lines.Where(l =>
                l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || l.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                || (l.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                    && !l.Contains("download_detour", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return important.Count > 0 ? string.Join(" | ", important.TakeLast(3)) : lines[^1];
    }

    private RoutingOptions GetRoutingOptions()
    {
        var tag = GetSelectedRoutingModeTag();
        var mode = tag switch
        {
            "proxy-list" => RoutingMode.ProxyList,
            "bypass-list" => RoutingMode.BypassList,
            "bypass-ru" => RoutingMode.BypassRu,
            "app-proxy" => RoutingMode.AppProxy,
            "app-bypass" => RoutingMode.AppBypass,
            _ => RoutingMode.Global
        };
        return new RoutingOptions
        {
            Mode = mode,
            Domains = RoutingOptions.ParseDomainList(DomainListBox.Text),
            Processes = RoutingOptions.ParseProcessList(AppListBox.Text)
        };
    }

    private static void TryDeleteRuntimeSecret(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a crashed core may still hold the file briefly.
        }
    }

    private void PersistSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось сохранить настройки: " + ex.Message);
        }
    }

    private void RefreshServerList()
    {
        var selectedUri = (ServerList.SelectedItem as ServerProfile)?.RawUri;
        IEnumerable<ServerProfile> list = _servers;

        if (_showFavoritesOnly)
            list = list.Where(s => s.IsFavorite);

        var sorted = list
            .OrderByDescending(s => s.IsFavorite)
            .ThenByDescending(s => s.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ServerList.ItemsSource = null;
        ServerList.ItemsSource = sorted;
        if (selectedUri is not null)
        {
            var again = sorted.FirstOrDefault(s => s.RawUri == selectedUri);
            if (again is not null)
                ServerList.SelectedItem = again;
        }
    }

    private void ApplyNameOverrides()
    {
        _settings.ServerNameOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _settings.FavoriteServerUris ??= new HashSet<string>(StringComparer.Ordinal);
        _settings.ServerLastSuccessUtc ??= new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var s in _servers)
        {
            s.OriginalName ??= s.Name;
            if (_settings.ServerNameOverrides.TryGetValue(s.RawUri, out var custom)
                && !string.IsNullOrWhiteSpace(custom))
            {
                s.Name = custom.Trim();
            }

            s.IsFavorite = _settings.FavoriteServerUris.Contains(s.RawUri);
            if (_settings.ServerLastSuccessUtc.TryGetValue(s.RawUri, out var unix))
                s.LastSuccessUtc = DateTimeOffset.FromUnixTimeSeconds(unix);
            else
                s.LastSuccessUtc = null;
        }
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && StartMinimizedCheck.IsChecked == true)
            _tray?.HideWindowToTray();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        if (StartMinimizedCheck.IsChecked == true)
        {
            e.Cancel = true;
            _tray?.HideWindowToTray();
            return;
        }

        Cleanup();
    }

    private void ExitApp()
    {
        _exitRequested = true;
        Cleanup();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Cleanup(bool persistFromUi = true)
    {
        _stoppingCores = true;
        _sessionEpoch++;
        _subUpdateTimer?.Stop();
        _healthTimer?.Stop();
        _pingCts?.Cancel();
        try { _proxy.DisableIfOwned(); }
        catch (Exception ex) { AppLogService.Error("Cleanup: системный прокси", ex); }
        try
        {
            _runner.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        try
        {
            _xray.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_awgConnected || _awg.IsTunnelRunning)
                _awg.DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        _networkMonitor?.Dispose();
        _networkMonitor = null;
        _killSwitch.Disarm();
        _awgConnected = false;
        _activeCore = ProxyCoreKind.SingBox;
        _runner.Dispose();
        _xray.Dispose();
        _tray?.Dispose();
        if (persistFromUi)
        {
            CaptureSettingsFromUi();
            PersistSettings();
        }
    }

    private void SetStatus(string text, bool important = true)
    {
        StatusText.Text = text;
        AutomationProperties.SetName(StatusText, "Статус: " + text);
        AutomationProperties.SetLiveSetting(
            StatusText,
            important ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (LooksLikeErrorStatus(text))
            AppLogService.Error(text);
    }

    private static bool LooksLikeErrorStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase)
               || text.Contains("не удалась", StringComparison.OrdinalIgnoreCase)
               || text.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
               || text.Contains("сразу завершился", StringComparison.OrdinalIgnoreCase);
    }
}
