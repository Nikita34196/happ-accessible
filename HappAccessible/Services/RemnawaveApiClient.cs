using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

public sealed record RemnawaveUser(
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
    string UserUuid,
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

    public async Task<RemnawaveUser> UpdateHwidLimitAsync(string userUuid, int hwidDeviceLimit,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["uuid"] = userUuid,
            ["hwidDeviceLimit"] = hwidDeviceLimit
        };
        using var req = new HttpRequestMessage(HttpMethod.Patch, "api/users") { Content = ToJson(body) };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        return ParseUser(json);
    }

    public async Task<IReadOnlyList<RemnawaveDevice>> GetDevicesAsync(string userUuid,
        CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/hwid/devices/" + Uri.EscapeDataString(userUuid), ct)
            .ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response))
            response = doc.RootElement;
        if (!response.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<RemnawaveDevice>();
        foreach (var d in devices.EnumerateArray())
        {
            list.Add(new RemnawaveDevice(
                GetStr(d, "hwid") ?? "",
                GetStr(d, "userUuid") ?? userUuid,
                GetStr(d, "platform"),
                GetStr(d, "osVersion"),
                GetStr(d, "deviceModel"),
                GetStr(d, "userAgent"),
                GetTime(d, "createdAt")));
        }

        return list;
    }

    public async Task DeleteDeviceAsync(string userUuid, string hwid, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["userUuid"] = userUuid,
            ["hwid"] = hwid
        };
        using var resp = await _http.PostAsync("api/hwid/devices/delete", ToJson(body), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(resp, json);
    }

    private static StringContent ToJson(object body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    private static void EnsureOk(HttpResponseMessage resp, string json)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var msg = json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m))
                msg = m.GetString() ?? json;
        }
        catch
        {
            // keep raw
        }

        if (msg.Length > 400)
            msg = msg[..400];
        throw new InvalidOperationException($"Remnawave API {(int)resp.StatusCode}: {msg}");
    }

    private static RemnawaveUser ParseUser(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var u = doc.RootElement.TryGetProperty("response", out var r) ? r : doc.RootElement;
        return new RemnawaveUser(
            GetStr(u, "uuid") ?? "",
            GetStr(u, "username") ?? "",
            GetStr(u, "shortUuid") ?? "",
            GetStr(u, "subscriptionUrl") ?? "",
            GetInt(u, "hwidDeviceLimit"),
            GetStr(u, "status"),
            GetTime(u, "expireAt"),
            GetTime(u, "onlineAt"));
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

    private static DateTimeOffset? GetTime(JsonElement e, string name)
    {
        var s = GetStr(e, name);
        return DateTimeOffset.TryParse(s, out var t) ? t : null;
    }
}
