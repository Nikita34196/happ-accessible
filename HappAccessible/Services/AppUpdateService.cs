using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HappAccessible.Services;

public sealed record AppReleaseInfo(
    string CurrentVersion,
    string LatestVersion,
    string? PortableZipUrl,
    string? SetupExeUrl,
    string ReleaseUrl,
    bool UpdateAvailable);

/// <summary>
/// Checks GitHub Releases for Happ Accessible and applies updates silently or in-place.
/// </summary>
public sealed class AppUpdateService
{
    public const string GitHubOwner = "Nikita34196";
    public const string GitHubRepo = "happ-accessible";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HappAccessible/" + GetCurrentVersion());
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        return http;
    }

    public static string GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null)
            return "0.0.0";
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task<AppReleaseInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = GetCurrentVersion();
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest", ct)
            .ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new AppReleaseInfo(current, current, null, null,
                $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases", false);
        }

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = CoreUpdateService.NormalizeTag(tag) ?? "";
        var htmlUrl = root.TryGetProperty("html_url", out var hu)
            ? hu.GetString() ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases"
            : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";

        string? zip = null;
        string? setup = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.IsNullOrEmpty(url))
                    continue;
                if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zip = url;
                else if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                         && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    setup = url;
            }
        }

        var available = CoreUpdateService.IsNewer(latest, current)
                        && (!string.IsNullOrEmpty(zip) || !string.IsNullOrEmpty(setup));
        return new AppReleaseInfo(current, latest, zip, setup, htmlUrl, available);
    }

    public static bool IsRunningFromInstallDir()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase)
                   || dir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool CanSelfUpdateInPlace()
    {
        try
        {
            var dir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            var probe = Path.Combine(dir, $".ha-write-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string UpdatesRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "updates");

    /// <summary>
    /// Downloads portable zip and prepares a script that replaces files after the app exits.
    /// </summary>
    public async Task<string> PreparePortableUpdateAsync(
        AppReleaseInfo info,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.PortableZipUrl))
            throw new InvalidOperationException("В релизе нет portable zip.");

        var updateRoot = UpdatesRoot;
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, $"HappAccessible-{info.LatestVersion}.zip");
        var extractDir = Path.Combine(updateRoot, "extract-" + info.LatestVersion);
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        progress?.Report($"Скачиваю Happ Accessible {info.LatestVersion}…");
        await using (var fs = File.Create(zipPath))
        {
            await using var remote = await Http.GetStreamAsync(info.PortableZipUrl, ct).ConfigureAwait(false);
            await remote.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        progress?.Report("Распаковываю обновление…");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var children = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        var payload = extractDir;
        if (children.Length == 1 && files.Length == 0)
            payload = children[0];

        var exe = Path.Combine(payload, "HappAccessible.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException("В архиве нет HappAccessible.exe.");

        var targetDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        var pid = Environment.ProcessId;
        var script = Path.Combine(updateRoot, "apply-update.ps1");
        var ps1 = $@"
$ErrorActionPreference = 'Stop'
$target = '{targetDir.Replace("'", "''")}'
$source = '{payload.Replace("'", "''")}'
$pidToWait = {pid}
while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}
Start-Sleep -Seconds 1
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
Start-Process -FilePath (Join-Path $target 'HappAccessible.exe')
";
        await File.WriteAllTextAsync(script, ps1, ct).ConfigureAwait(false);
        progress?.Report("Обновление готово. Приложение перезапустится.");
        return script;
    }

    public async Task<string> DownloadSetupAsync(
        AppReleaseInfo info,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.SetupExeUrl))
            throw new InvalidOperationException("В релизе нет Setup.exe.");

        Directory.CreateDirectory(UpdatesRoot);
        var setupPath = Path.Combine(UpdatesRoot, $"HappAccessible-Setup-{info.LatestVersion}.exe");
        progress?.Report($"Скачиваю установщик {info.LatestVersion}…");
        await using (var fs = File.Create(setupPath))
        {
            await using var remote = await Http.GetStreamAsync(info.SetupExeUrl, ct).ConfigureAwait(false);
            await remote.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        return setupPath;
    }

    public static void LaunchUpdaterScript(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        });
    }

    /// <summary>Silent Inno Setup upgrade in place.</summary>
    public static void LaunchSetupSilent(string setupPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string QuoteCmd(string path) => path.Replace("\"", "");
}
