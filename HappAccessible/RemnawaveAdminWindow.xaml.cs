using System.Windows;
using HappAccessible.Services;

namespace HappAccessible;

public partial class RemnawaveAdminWindow : Window
{
    private readonly AppSettings _settings;
    private RemnawaveUser? _user;
    private readonly List<DeviceRow> _devices = [];

    public string? AppliedSubscriptionUrl { get; private set; }

    private sealed class DeviceRow
    {
        public required RemnawaveDevice Device { get; init; }
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    public RemnawaveAdminWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        PanelUrlBox.Text = string.IsNullOrWhiteSpace(settings.RemnawavePanelUrl)
            ? GuessPanelUrl(settings.SubscriptionInput)
            : settings.RemnawavePanelUrl;
        if (!string.IsNullOrEmpty(settings.RemnawaveApiToken))
            ApiTokenBox.Password = settings.RemnawaveApiToken;

        LookupBox.Text = TryExtractShortUuid(settings.SubscriptionInput) ?? "";
        NewUserBox.Text = "user" + DateTime.Now.ToString("MMddHHmm");
    }

    private RemnawaveApiClient CreateClient()
    {
        var url = PanelUrlBox.Text.Trim();
        var token = ApiTokenBox.Password;
        if (string.IsNullOrWhiteSpace(token))
            token = _settings.RemnawaveApiToken ?? "";
        return new RemnawaveApiClient(url, token);
    }

    private void SaveAccess_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.RemnawavePanelUrl = PanelUrlBox.Text.Trim();
        _settings.RemnawaveApiToken = ApiTokenBox.Password;
        _settings.Save();
        SetStatus("Доступ сохранён локально.");
    }

    private async void CreateLink_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var username = NewUserBox.Text.Trim();
            if (username.Length < 3)
            {
                SetStatus("Имя пользователя: минимум 3 символа (латиница, цифры, _ -).");
                return;
            }

            if (!int.TryParse(DaysBox.Text.Trim(), out var days) || days is < 1 or > 3650)
            {
                SetStatus("Укажите срок в днях от 1 до 3650.");
                return;
            }

            if (!int.TryParse(NewLimitBox.Text.Trim(), out var limit) || limit < 0)
            {
                SetStatus("Укажите лимит устройств (0 = без лимита / как в панели).");
                return;
            }

            SetStatus("Создаю пользователя…");
            var client = CreateClient();
            var user = await client.CreateUserAsync(username, DateTimeOffset.UtcNow.AddDays(days), limit);
            _user = user;
            NewLinkBox.Text = user.SubscriptionUrl;
            LookupBox.Text = user.ShortUuid;
            LimitBox.Text = (user.HwidDeviceLimit ?? limit).ToString();
            UpdateUserInfo();
            SetStatus($"Готово: {user.Username}. Ссылка ниже — копируйте или примените в Happ.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка создания: " + ex.Message);
            AppLogService.Error("Remnawave create user", ex);
        }
    }

    private void CopyLink_OnClick(object sender, RoutedEventArgs e)
    {
        var link = NewLinkBox.Text.Trim();
        if (string.IsNullOrEmpty(link))
        {
            SetStatus("Сначала создайте ссылку.");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(link);
            SetStatus("Ссылка скопирована в буфер.");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось скопировать: " + ex.Message);
        }
    }

    private void ApplyLink_OnClick(object sender, RoutedEventArgs e)
    {
        var link = NewLinkBox.Text.Trim();
        if (string.IsNullOrEmpty(link))
        {
            SetStatus("Сначала создайте ссылку.");
            return;
        }

        AppliedSubscriptionUrl = link;
        DialogResult = true;
        Close();
    }

    private async void LoadUser_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = LookupBox.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Введите short UUID или имя пользователя.");
                return;
            }

            SetStatus("Загружаю…");
            var client = CreateClient();
            _user = await ResolveUserAsync(client, key);
            LimitBox.Text = (_user.HwidDeviceLimit ?? 0).ToString();
            if (!string.IsNullOrEmpty(_user.SubscriptionUrl))
                NewLinkBox.Text = _user.SubscriptionUrl;
            UpdateUserInfo();
            SetStatus($"Загружен: {_user.Username}.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка загрузки: " + ex.Message);
            AppLogService.Error("Remnawave load user", ex);
        }
    }

    private static async Task<RemnawaveUser> ResolveUserAsync(RemnawaveApiClient client, string key)
    {
        // Short UUIDs often look like "kmqj9-FVB1aCGFcm"; usernames are usually alphanumeric/_-
        Exception? first = null;
        try
        {
            return await client.GetByShortUuidAsync(key);
        }
        catch (Exception ex)
        {
            first = ex;
        }

        try
        {
            return await client.GetByUsernameAsync(key);
        }
        catch (Exception)
        {
            throw first ?? new InvalidOperationException("Пользователь не найден.");
        }
    }

    private async void SetLimit_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_user is null)
            {
                SetStatus("Сначала загрузите пользователя.");
                return;
            }

            if (!int.TryParse(LimitBox.Text.Trim(), out var limit) || limit < 0)
            {
                SetStatus("Лимит устройств: целое число ≥ 0.");
                return;
            }

            SetStatus("Меняю лимит…");
            var client = CreateClient();
            _user = await client.UpdateHwidLimitAsync(_user.Id, limit);
            LimitBox.Text = (_user.HwidDeviceLimit ?? limit).ToString();
            UpdateUserInfo();
            SetStatus($"Лимит устройств: {_user.HwidDeviceLimit}.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка лимита: " + ex.Message);
            AppLogService.Error("Remnawave set limit", ex);
        }
    }

    private async void ShowDevices_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка устройств: " + ex.Message);
            AppLogService.Error("Remnawave devices", ex);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_user is null)
        {
            SetStatus("Сначала загрузите пользователя.");
            return;
        }

        SetStatus("Загружаю устройства…");
        var client = CreateClient();
        var devices = await client.GetDevicesAsync(_user.Id);
        _devices.Clear();
        foreach (var d in devices)
        {
            var label = string.Join(" · ", new[]
            {
                d.Platform,
                d.DeviceModel,
                d.OsVersion,
                Truncate(d.Hwid, 20),
                d.CreatedAt?.ToLocalTime().ToString("g")
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(label))
                label = Truncate(d.Hwid, 40);
            _devices.Add(new DeviceRow { Device = d, Display = label });
        }

        DeviceList.DisplayMemberPath = nameof(DeviceRow.Display);
        DeviceList.ItemsSource = null;
        DeviceList.ItemsSource = _devices;
        if (_devices.Count > 0)
            DeviceList.SelectedIndex = 0;
        SetStatus($"Устройств: {_devices.Count} (лимит {_user.HwidDeviceLimit?.ToString() ?? "—"}).");
    }

    private async void DeleteDevice_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_user is null)
            {
                SetStatus("Сначала загрузите пользователя.");
                return;
            }

            if (DeviceList.SelectedItem is not DeviceRow row || string.IsNullOrWhiteSpace(row.Device.Hwid))
            {
                SetStatus("Выберите устройство в списке (стрелки ↑↓), затем удалите.");
                System.Windows.MessageBox.Show(
                    this,
                    "Сначала выберите устройство в списке.",
                    "Удаление устройства",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SetStatus("Удаляю устройство…");
            var client = CreateClient();
            await client.DeleteDeviceAsync(_user.Id, row.Device.Hwid);
            SetStatus("Устройство удалено. Обновляю список…");
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка удаления: " + ex.Message);
            AppLogService.Error("Remnawave delete device", ex);
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Ошибка удаления устройства",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void DeleteAllDevices_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_user is null)
            {
                SetStatus("Сначала загрузите пользователя.");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                this,
                $"Удалить все HWID-устройства пользователя {_user.Username}?",
                "Удалить все устройства",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            SetStatus("Удаляю все устройства…");
            var client = CreateClient();
            await client.DeleteAllDevicesAsync(_user.Id);
            await RefreshDevicesAsync();
            SetStatus("Все устройства удалены.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка удаления всех: " + ex.Message);
            AppLogService.Error("Remnawave delete all devices", ex);
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Ошибка удаления устройств",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateUserInfo()
    {
        if (_user is null)
        {
            UserInfoText.Text = "Пользователь не загружен.";
            return;
        }

        UserInfoText.Text =
            $"{_user.Username} · id {_user.Id} · статус {_user.Status ?? "—"} · устройств лимит {_user.HwidDeviceLimit?.ToString() ?? "—"} · " +
            $"до {_user.ExpireAt?.ToLocalTime().ToString("d") ?? "—"} · online {_user.OnlineAt?.ToLocalTime().ToString("g") ?? "—"} · " +
            $"short {_user.ShortUuid}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string GuessPanelUrl(string? subscription)
    {
        if (string.IsNullOrWhiteSpace(subscription))
            return "https://194.87.49.190.sslip.io";
        try
        {
            var u = new Uri(subscription.Trim());
            return u.GetLeftPart(UriPartial.Authority);
        }
        catch
        {
            return "https://194.87.49.190.sslip.io";
        }
    }

    private static string? TryExtractShortUuid(string? subscription)
    {
        if (string.IsNullOrWhiteSpace(subscription))
            return null;
        try
        {
            var u = new Uri(subscription.Trim());
            var q = u.Query.TrimStart('?');
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("id", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(kv[1]);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
