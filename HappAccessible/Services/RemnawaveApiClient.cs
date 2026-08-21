using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

public sealed record RemnawaveUser(
    long Id,
    string Uuid,
    string Username,
    string ShortUuid,
    string SubscriptionUrl,
    int? HwidDeviceLimit,
    string? Status,
    DateTimeOffset? ExpireAt,
    DateTimeOffset? OnlineAt);

public sealed record RemnawaveDevice(
    string Hwid,
    long UserId,
    string? Platform,
    string? OsVersion,
    string? DeviceModel,
    string? UserAgent,
    DateTimeOffset? CreatedAt);

public sealed class RemnawaveApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public RemnawaveApiClient(string baseUrl, string apiToken)
    {
        baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Укажите URL панели Remnawave.");
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new ArgumentException("Укажите API-токен Remnawave.");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45), BaseAddress = new Uri(baseUrl + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HappAccessible/" + AppUpdateService.GetCurrentVersion());
    }

    public async Task<RemnawaveUser> CreateUserAsync(
        string username,
        DateTimeOffset expireAt,
        int hwidDeviceLimit,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["expireAt"] = expireAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["status"] = "ACTIVE",
            ["hwidDeviceLimit"] = hwidDeviceLimit,
            ["trafficLimitBytes"] = 0,
            ["trafficLimitStrategy"] = "NO_RESET"
        };
        using var resp = await _http.PostAsync("api/users", ToJson(body), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseUser(json);
    }

    public async Task<RemnawaveUser> GetByShortUuidAsync(string shortUuid, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/users/by-short-uuid/" + Uri.EscapeDataString(shortUuid), ct)
            .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseUser(json);
    }

    public async Task<RemnawaveUser> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/users/by-username/" + Uri.EscapeDataString(username), ct)
            .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseUser(json);
    }

    public async Task<RemnawaveUser> UpdateHwidLimitAsync(long userId, int hwidDeviceLimit,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["hwidDeviceLimit"] = hwidDeviceLimit
        };
        using var req = new HttpRequestMessage(HttpMethod.Patch, "api/users") { Content = ToJson(body) };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseUser(json);
    }

    public async Task<IReadOnlyList<RemnawaveDevice>> GetDevicesAsync(long userId,
        CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/hwid/devices/" + userId, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseDevices(json, userId);
    }

    public async Task DeleteDeviceAsync(long userId, string hwid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hwid))
            throw new ArgumentException("Пустой HWID устройства.");

        var body = new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["hwid"] = hwid
        };
        using var resp = await _http.PostAsync("api/hwid/devices/delete", ToJson(body), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
    }

    public async Task DeleteAllDevicesAsync(long userId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["userId"] = userId };
        using var resp = await _http.PostAsync("api/hwid/devices/delete-all", ToJson(body), ct)
            .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
    }

    private static StringContent ToJson(object body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    private static void EnsureOk(HttpResponseMessage resp, string json)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var msg = ExtractError(json);
        if (msg.Length > 400)
            msg = msg[..400];
        throw new InvalidOperationException($"Remnawave API {(int)resp.StatusCode}: {msg}");
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m))
            {
                if (m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? json;
                if (m.ValueKind == JsonValueKind.Array)
                    return string.Join("; ", m.EnumerateArray().Select(x => x.ToString()));
            }

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? json;
        }
        catch
        {
            // keep raw
        }

        return string.IsNullOrWhiteSpace(json) ? "unknown error" : json;
    }

    private static RemnawaveUser ParseUser(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var u = doc.RootElement.TryGetProperty("response", out var r) ? r : doc.RootElement;
        var id = GetLong(u, "id") ?? 0;
        if (id <= 0)
            throw new InvalidOperationException("Ответ Remnawave без числового id пользователя.");

        return new RemnawaveUser(
            id,
            GetStr(u, "uuid") ?? "",
            GetStr(u, "username") ?? "",
            GetStr(u, "shortUuid") ?? "",
            GetStr(u, "subscriptionUrl") ?? "",
            GetInt(u, "hwidDeviceLimit"),
            GetStr(u, "status"),
            GetTime(u, "expireAt"),
            GetTime(u, "onlineAt"));
    }

    private static IReadOnlyList<RemnawaveDevice> ParseDevices(string json, long fallbackUserId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response))
            response = doc.RootElement;

        JsonElement devicesEl;
        if (response.ValueKind == JsonValueKind.Array)
            devicesEl = response;
        else if (response.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            devicesEl = devices;
        else
            return [];

        var list = new List<RemnawaveDevice>();
        foreach (var d in devicesEl.EnumerateArray())
        {
            list.Add(new RemnawaveDevice(
                GetStr(d, "hwid") ?? "",
                GetLong(d, "userId") ?? fallbackUserId,
                GetStr(d, "platform"),
                GetStr(d, "osVersion"),
                GetStr(d, "deviceModel"),
                GetStr(d, "userAgent"),
                GetTime(d, "createdAt")));
        }

        return list;
    }

    private static string? GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out n))
            return n;
        return null;
    }

    private static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out n))
            return n;
        return null;
    }

    private static DateTimeOffset? GetTime(JsonElement e, string name)
    {
        var s = GetStr(e, name);
        return DateTimeOffset.TryParse(s, out var t) ? t : null;
    }
}
