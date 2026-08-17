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
    private int _healthFailStreak;
    private bool _connectBusy;
    private bool _awgConnected;
    private bool _healthTickRunning;
    private bool _subUpdateTickRunning;
    private int _sessionEpoch; // bumped on Disconnect to cancel in-flight failover/connect follow-ups
    private enum ConnectOutcome { Success, Failed, Busy, Cancelled }

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
        SelectRoutingMode(_settings.RoutingMode);
        UpdateDomainListVisibility();
        UpdateSubscriptionInfo();
        _loadingUi = false;

        if (_settings.UseTun && ElevationHelper.IsElevated)
            Title = "Happ Accessible (администратор)";

        _tray = new TrayService(this);
        _tray.SnapshotProvider = BuildTraySnapshot;
        _tray.ShowRequested += () => Dispatcher.Invoke(() => _tray.ShowWindow());
        _tray.ConnectRequested += () => Dispatcher.InvokeAsync(async () => await ConnectAsync());
        _tray.DisconnectRequested += () => Dispatcher.InvokeAsync(async () => await DisconnectAsync());
        _tray.RefreshSubscriptionRequested += () => Dispatcher.InvokeAsync(async () =>
        {
            await LoadSubscriptionAsync(announce: true);
            _tray?.Notify($"Серверов в списке: {_servers.Count}");
        });
        _tray.ServerConnectRequested += uri => Dispatcher.InvokeAsync(async () =>
            await ConnectServerFromTrayAsync(uri));
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _tray.SetTooltip("Happ Accessible — не подключено");

        // Recover from crash: leftover system proxy / AmneziaWG tunnel
        _proxy.ClearStaleOwnedProxy(_settings.MixedPort, EngineOptions.DefaultMixedPort, 10808);
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

        if (_settings.StartMinimizedToTray)
            _tray.HideWindowToTray();

        _ = CheckAndUpdateCoresOnStartupAsync();
    }

    private async Task CheckAndUpdateCoresOnStartupAsync()
    {
        try
        {
            try { await _runner.EnsureBinaryAsync(); } catch { /* may download */ }
            var state = CoreVersionsState.Load();
            var localSb = state.SingBox ?? CoreUpdateService.NormalizeTag(_runner.CoreVersion);
            var localXr = state.Xray;
            var localAwg = state.AmneziaWg ?? _awg.InstalledVersion;

            SetStatus("Проверка обновлений ядер (GitHub)…");
            var infos = await _coreUpdates.CheckAllAsync(localSb, localXr, localAwg);
            _settings.LastCoreCheckUtc = DateTime.UtcNow;
            PersistSettings();

            var updates = infos.Where(i => i.UpdateAvailable && !string.IsNullOrEmpty(i.DownloadUrl)).ToList();
            if (updates.Count == 0)
            {
                string Ver(CoreReleaseInfo i) =>
                    string.IsNullOrEmpty(i.LocalVersion) ? i.RemoteVersion : i.LocalVersion;
                SetStatus(
                    $"Ядра актуальны: sing-box {Ver(infos[0])}, Xray {Ver(infos[1])}, AmneziaWG {Ver(infos[2])}.");
                return;
            }

            var summary = string.Join("; ",
                updates.Select(u =>
                    $"{u.Id} {(string.IsNullOrEmpty(u.LocalVersion) ? "—" : u.LocalVersion)} → {u.RemoteVersion}"));

            if (!_settings.AutoUpdateCores)
            {
                SetStatus($"Доступны обновления ядер: {summary}. Меню Справка → Проверить обновления ядер.");
                return;
            }

            if (_connectedServer is not null || _connectBusy || _awgConnected)
            {
                SetStatus($"Доступны обновления ядер (отложено, есть подключение): {summary}.");
                return;
            }

            SetStatus($"Обновляю ядра: {summary}…");
            foreach (var u in updates)
            {
                var progress = new Progress<string>(SetStatus);
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

            SetStatus($"Ядра обновлены: {summary}.");
        }
        catch (Exception ex)
        {
            SetStatus("Проверка ядер не удалась: " + ex.Message);
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
            Interval = TimeSpan.FromSeconds(75)
        };
        _healthTimer.Tick += async (_, _) =>
        {
            if (_healthTickRunning)
                return;
            _healthTickRunning = true;
            try { await HealthCheckTickAsync(); }
            finally { _healthTickRunning = false; }
        };
        if (_settings.AutoWhitelistFailover)
            _healthTimer.Start();
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

    private async Task HealthCheckTickAsync()
    {
        if (!_settings.AutoWhitelistFailover || _connectedServer is null || _connectBusy || _failoverBusy)
            return;

        // Do not auto-switch away from AmneziaWG on flaky HTTP probes
        if (_awgConnected || _connectedServer.Protocol == "amneziawg")
            return;

        if (GetSelectedRoutingModeTag() is "proxy-list" or "app-proxy" or "app-bypass")
            return;

        var ok = _activeCore == ProxyCoreKind.Xray
            ? await _xray.ProbeConnectivityAsync()
            : await _runner.ProbeConnectivityAsync();
        if (_connectedServer is null || _connectBusy || _failoverBusy)
            return;
        if (ok)
        {
            _healthFailStreak = 0;
            return;
        }

        _healthFailStreak++;
        if (_healthFailStreak < 2)
        {
            SetStatus("Проверка связи: сбой, повторю ещё раз…");
            return;
        }

        _healthFailStreak = 0;
        var failed = _connectedServer;
        if (failed is null || failed.Protocol == "amneziawg")
            return;
        SetStatus($"Туннель не отвечает ({failed.Name}). Ищу сервер обхода белых списков…");
        await FailoverToWhitelistAsync(excludeUri: failed.RawUri);
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

        if (e.Key == Key.C)
        {
            e.Handled = true;
            _ = ConnectAsync(allowFailover: true);
        }
        else if (e.Key == Key.D)
        {
            e.Handled = true;
            _ = DisconnectAsync();
        }
    }

    private async void MenuEditSubscription_OnClick(object sender, RoutedEventArgs e) =>
        await EditSubscriptionAsync();

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
        SetStatus(
            "Happ Accessible 0.3.6 — dual-core + меню серверов в трее. " +
            "Ctrl+Shift+C/D — подключить/отключить. ПКМ по значку → Серверы.");
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
        foreach (var cfg in AmneziaWgConfigStore.ListImported())
            _servers.Add(cfg.ToProfile());
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

            string body;
            var fromCache = false;
            try
            {
                body = await _fetcher.FetchAsync(input, _settings);
            }
            catch (Exception fetchEx)
            {
                var cached = SubscriptionFetcher.TryLoadCacheOnly(input);
                if (cached is null)
                    throw;

                body = cached;
                fromCache = true;
                if (announce)
                    SetStatus("Сеть недоступна или лимит запросов. Загружаю сохранённый кэш. " + fetchEx.Message);
            }

            var parsed = SubscriptionParser.Parse(body);
            foreach (var s in parsed)
                s.IsWhitelistBypass = ServerClassifier.IsWhitelistBypass(s);

            _servers.Clear();
            _servers.AddRange(parsed);
            foreach (var cfg in AmneziaWgConfigStore.ListImported())
                _servers.Add(cfg.ToProfile());
            RefreshServerList();

            _settings.SubscriptionInput = input;
            PersistSettings();
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
                SetStatus(fromCache
                    ? $"Из кэша: {_servers.Count} (обход БС: {wl}, AWG: {awg})."
                    : $"Загружено: {_servers.Count} (обход БС: {wl}, AmneziaWG: {awg}).");
            }
            else if (!quiet)
            {
                SetStatus($"Подписка обновлена: {_servers.Count} (обход БС: {wl}, AWG: {awg}).");
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
                SetStatus("Ошибка загрузки: " + ex.Message);
        }
    }

    private void UpdateSubscriptionInfo()
    {
        if (SubscriptionInfoText is null)
            return;

        var raw = _settings.SubscriptionInput?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            SubscriptionInfoText.Text = "Подписка не задана. Alt, затем П — меню «Подписка».";
            return;
        }

        if (AmneziaWgConfigStore.LooksLikeConf(raw))
        {
            SubscriptionInfoText.Text = "Сохранён текст AmneziaWG-конфига (скрыт). Меню: Alt, П.";
            return;
        }

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string host;
            try { host = new Uri(raw).Host; }
            catch { host = "ссылка"; }
            SubscriptionInfoText.Text = $"Подписка сохранена ({host}). Ссылка скрыта. Обновить: F5 или меню.";
            return;
        }

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        SubscriptionInfoText.Text = lines > 1
            ? $"Подписка сохранена (список, строк: {lines}). Содержимое скрыто."
            : "Подписка сохранена. Содержимое скрыто.";
    }

    private async void PingButton_OnClick(object sender, RoutedEventArgs e)
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
        SetStatus($"Пинг {_servers.Count} серверов…");

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
                SetStatus($"Пинг готов: отвечают {ok} из {_servers.Count}. Лучший: {best.Name}, {best.LatencyMs} мс.");
                if (ServerList.SelectedItem is null)
                    ServerList.SelectedItem = best;
            }
            else
            {
                SetStatus($"Пинг готов: ни один сервер не ответил за 3 секунды ({_servers.Count} шт.).");
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

    private async void ConnectButton_OnClick(object sender, RoutedEventArgs e) =>
        await ConnectAsync(allowFailover: true);

    private async void DisconnectButton_OnClick(object sender, RoutedEventArgs e) => await DisconnectAsync();

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
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConnectAsync(allowFailover: true);
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
        CaptureSettingsFromUi();
        PersistSettings();
        RestartBackgroundTimers();
    }

    private void TunCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;

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
        if (_connectBusy)
            return ConnectOutcome.Busy;

        _connectBusy = true;
        var epoch = _sessionEpoch;
        try
        {
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

            SetStatus(useTun
                ? $"Запуск TUN ({server.Name})… Проверка связи…"
                : $"Подключение к «{server.Name}»… Проверка связи…");

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
                _connectedServer = null;
                var failHint = BuildCoreHint(server, failed: true);
                SetStatus(
                    $"Сервер «{server.Name}» не отвечает через туннель (ядро: sing-box). {failHint}" +
                    "Лог: " + TruncateStatus(_runner.RecentLog));
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
            _healthFailStreak = 0;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();

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
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _proxy.DisableIfOwned();
            await _runner.StopAsync();
            await _xray.StopAsync();
            _connectedServer = null;
            _awgConnected = false;
            SetStatus("Ошибка подключения: " + ex.Message);
            _tray?.SetTooltip("Happ Accessible — ошибка");
            return ConnectOutcome.Failed;
        }
        finally
        {
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
            SetStatus($"Xray: «{server.Name}»… Проверка связи…");
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
            _healthFailStreak = 0;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();

            var core = string.IsNullOrWhiteSpace(_xray.CoreVersion) ? "Xray" : $"Xray {_xray.CoreVersion}";
            var mode = useProxy
                ? "системный прокси"
                : $"локальный HTTP 127.0.0.1:{engine.MixedPort}";
            var hint = BuildCoreHint(server, failed: false);
            var msg = $"Подключено: {server.Name}. Ядро: {core}. {mode}.{hint} Связь проверена.";
            SetStatus(msg);
            _tray?.SetTooltip($"Happ Accessible — {server.Name} (Xray)");
            _tray?.Notify(msg);
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _proxy.DisableIfOwned();
            await _xray.StopAsync();
            _connectedServer = null;
            SetStatus("Ошибка Xray: " + ex.Message);
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
            _healthFailStreak = 0;
            _settings.LastServerUri = server.RawUri;
            _settings.LastServerName = server.Name;
            ServerList.SelectedItem = server;
            CaptureSettingsFromUi();
            PersistSettings();

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
            return ConnectOutcome.Success;
        }
        catch (Exception ex)
        {
            _awgConnected = false;
            _connectedServer = null;
            try { await _awg.DisconnectAsync(); } catch { /* ignore */ }
            SetStatus("Ошибка AmneziaWG: " + ex.Message);
            _tray?.SetTooltip("Happ Accessible — ошибка AWG");
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

            SetStatus("Автопереключение не удалось: ни один кандидат обхода БС не дал трафик.");
            _tray?.SetTooltip("Happ Accessible — нет связи");
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

    private async Task DisconnectAsync()
    {
        try
        {
            _sessionEpoch++;
            _healthFailStreak = 0;
            _proxy.DisableIfOwned();
            await _runner.StopAsync();
            await _xray.StopAsync();
            if (_awgConnected || _awg.IsTunnelRunning)
                await _awg.DisconnectAsync();
            _awgConnected = false;
            _connectedServer = null;
            _activeCore = ProxyCoreKind.SingBox;
            SetStatus("Отключено.");
            _tray?.SetTooltip("Happ Accessible — не подключено");
            _tray?.Notify("Отключено.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка отключения: " + ex.Message);
        }
    }

    private void CaptureSettingsFromUi()
    {
        if (SystemProxyCheck is null || TunCheck is null
            || AutoConnectCheck is null || StartMinimizedCheck is null || ServerList is null
            || RoutingModeBox is null || DomainListBox is null || AppListBox is null
            || AutoUpdateSubCheck is null || AutoWhitelistCheck is null
            || TunStackBox is null || MixedPortBox is null
            || ProxyCoreBox is null || AutoUpdateCoresCheck is null)
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
        _settings.AutoConnect = AutoConnectCheck.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCheck.IsChecked == true;
        _settings.AutoUpdateSubscription = AutoUpdateSubCheck.IsChecked == true;
        _settings.AutoWhitelistFailover = AutoWhitelistCheck.IsChecked == true;
        _settings.RoutingMode = GetSelectedRoutingModeTag();
        _settings.DomainList = DomainListBox.Text ?? "";
        _settings.AppList = AppListBox.Text ?? "";
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
        return new EngineOptions
        {
            MixedPort = EngineOptions.ClampPort(_settings.MixedPort),
            TunStack = EngineOptions.NormalizeTunStack(_settings.TunStack)
        };
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

    /// <summary>Hints for Reality/Vision when sing-box may struggle (Vireo keeps Xray for that).</summary>
    private static string BuildCoreHint(ServerProfile server, bool failed)
    {
        var uri = server.RawUri ?? "";
        var reality = uri.Contains("security=reality", StringComparison.OrdinalIgnoreCase)
                      || uri.Contains("pbk=", StringComparison.OrdinalIgnoreCase);
        var vision = uri.Contains("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase)
                     || uri.Contains("flow=xtls", StringComparison.OrdinalIgnoreCase);

        if (!reality && !vision)
            return "";

        if (failed)
        {
            return " Узел Reality/Vision: попробуйте ядро Xray (Авто/Xray), другой сервер, " +
                   "или смените TUN stack / порт mixed. ";
        }

        return " (Reality/Vision: в Авто обычно Xray.)";
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
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers.ToList();
        if (selectedUri is not null)
        {
            var again = _servers.FirstOrDefault(s => s.RawUri == selectedUri);
            if (again is not null)
                ServerList.SelectedItem = again;
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
        _sessionEpoch++;
        _subUpdateTimer?.Stop();
        _healthTimer?.Stop();
        _pingCts?.Cancel();
        _proxy.DisableIfOwned();
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

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        AutomationProperties.SetName(StatusText, "Статус: " + text);
        // Do not Focus() — steals keyboard from NVDA users mid-interaction
    }
}
