using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

public sealed class CoreVersionsState
{
    public string? SingBox { get; set; }
    public string? Xray { get; set; }
    public string? AmneziaWg { get; set; }

    private static string PathFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "tools", "core-versions.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CoreVersionsState Load()
    {
        try
        {
            if (!File.Exists(PathFile))
                return new CoreVersionsState();
            return JsonSerializer.Deserialize<CoreVersionsState>(File.ReadAllText(PathFile), JsonOptions)
                   ?? new CoreVersionsState();
        }
        catch
        {
            return new CoreVersionsState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // ignore
        }
    }
}

public sealed record CoreReleaseInfo(string Id, string LocalVersion, string RemoteVersion, string DownloadUrl, bool UpdateAvailable);

public sealed class CoreUpdateService
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HappAccessible/" + AppUpdateService.GetCurrentVersion());
        return http;
    }

    public async Task<IReadOnlyList<CoreReleaseInfo>> CheckAllAsync(
        string? localSingBox,
        string? localXray,
        string? localAwg,
        CancellationToken ct = default)
    {
        var list = new List<CoreReleaseInfo>();
        list.Add(await CheckSingBoxAsync(localSingBox, ct).ConfigureAwait(false));
        list.Add(await CheckXrayAsync(localXray, ct).ConfigureAwait(false));
        list.Add(await CheckAmneziaWgAsync(localAwg, ct).ConfigureAwait(false));
        return list;
    }

    public async Task<CoreReleaseInfo> CheckSingBoxAsync(string? local, CancellationToken ct)
    {
        // Fork with XHTTP (+ AWG2) — same approach as Vireo/singbox-launcher (Leadaxe/sing-box-lx)
        var (tag, url) = await LatestAssetAsync(
            "Leadaxe/sing-box-lx",
            name => name.Contains("windows-amd64", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("legacy", StringComparison.OrdinalIgnoreCase),
            ct).ConfigureAwait(false);
        var remote = NormalizeTag(tag) ?? "";
        local = NormalizeTag(local) ?? "";
        // Stock SagerNet builds lack xhttp — treat any non-lx local as outdated
        var update = IsNewer(remote, local) || (IsLxBuild(remote) && !IsLxBuild(local) && !string.IsNullOrEmpty(remote));
        return new CoreReleaseInfo("sing-box", local, remote, url, update);
    }

    public async Task<CoreReleaseInfo> CheckXrayAsync(string? local, CancellationToken ct)
    {
        var (tag, url) = await LatestAssetAsync(
            "XTLS/Xray-core",
            name => name.Equals("Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase)
                    || (name.Contains("windows-64", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("windows-64.zip.dgst", StringComparison.OrdinalIgnoreCase)),
            ct).ConfigureAwait(false);
        var remote = NormalizeTag(tag) ?? "";
        local = NormalizeTag(local) ?? "";
        return new CoreReleaseInfo("xray", local, remote, url, IsNewer(remote, local));
    }

    public async Task<CoreReleaseInfo> CheckAmneziaWgAsync(string? local, CancellationToken ct)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "amd64"
        };

        using var release = await Http.GetAsync(
            "https://api.github.com/repos/amnezia-vpn/amneziawg-windows-client/releases/latest", ct)
            .ConfigureAwait(false);
        release.EnsureSuccessStatusCode();
        await using var stream = await release.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string? url = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains($"amneziawg-{arch}-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("windows7", StringComparison.OrdinalIgnoreCase))
            {
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        url ??= "";
        var remote = NormalizeTag(tag) ?? "";
        local = NormalizeTag(local) ?? "";
        return new CoreReleaseInfo("amneziawg", local, remote, url, IsNewer(remote, local));
    }

    private static async Task<(string tag, string url)> LatestAssetAsync(
        string repo, Func<string, bool> assetMatch, CancellationToken ct)
    {
        using var release = await Http.GetAsync(
            $"https://api.github.com/repos/{repo}/releases/latest", ct).ConfigureAwait(false);
        release.EnsureSuccessStatusCode();
        await using var stream = await release.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!assetMatch(name))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!string.IsNullOrEmpty(url))
                return (tag, url);
        }

        throw new InvalidOperationException($"В релизе {repo} нет подходящего asset.");
    }

    public static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        tag = tag.Trim();
        if (tag.StartsWith('v') || tag.StartsWith('V'))
            tag = tag[1..];
        // "sing-box version 1.13.15" → last token
        if (tag.Contains(' '))
        {
            var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            tag = parts[^1];
        }

        return tag;
    }

    public static bool IsLxBuild(string? version)
    {
        version = NormalizeTag(version);
        return !string.IsNullOrEmpty(version)
               && version.Contains("-lx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if remote looks newer than local (empty local → update available).</summary>
    public static bool IsNewer(string? remote, string? local)
    {
        remote = NormalizeTag(remote);
        local = NormalizeTag(local);
        if (string.IsNullOrEmpty(remote))
            return false;
        if (string.IsNullOrEmpty(local))
            return true;
        if (string.Equals(remote, local, StringComparison.OrdinalIgnoreCase))
            return false;

        // Migrate stock → lx fork even when base numbers look similar
        if (IsLxBuild(remote) && !IsLxBuild(local))
            return true;

        var (rBase, rLx) = SplitLxVersion(remote);
        var (lBase, lLx) = SplitLxVersion(local);
        if (Version.TryParse(PadVersion(rBase), out var r)
            && Version.TryParse(PadVersion(lBase), out var l))
        {
            var cmp = r.CompareTo(l);
            if (cmp != 0)
                return cmp > 0;
            return rLx > lLx;
        }

        return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static (string baseVersion, int lxPatch) SplitLxVersion(string v)
    {
        var i = v.IndexOf("-lx.", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            i = v.IndexOf("-lx", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return (v, 0);
        var bas = v[..i];
        var rest = v[(i + 1)..]; // lx.26 or lx26
        var digits = new string(rest.Where(char.IsDigit).ToArray());
        _ = int.TryParse(digits, out var n);
        return (bas, n);
    }

    private static string PadVersion(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length < 2)
        {
            v += ".0";
            parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        }

        return v;
    }
}
