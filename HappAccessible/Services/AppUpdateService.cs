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
        var ps1 = BuildPortableUpdateScript(
            targetDir,
            payload,
            zipPath,
            updateRoot,
            pid);
        await File.WriteAllTextAsync(script, ps1, ct).ConfigureAwait(false);
        progress?.Report("Обновление готово. Приложение перезапустится.");
        return script;
    }

    /// <summary>
    /// Downloads Setup.exe and prepares a script that runs it silently, then
    /// removes update artifacts from %LocalAppData%\HappAccessible\updates.
    /// </summary>
    public async Task<string> PrepareSetupUpdateAsync(
        AppReleaseInfo info,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var setupPath = await DownloadSetupAsync(info, progress, ct).ConfigureAwait(false);
        var updateRoot = UpdatesRoot;
        var script = Path.Combine(updateRoot, "apply-setup.ps1");
        var ps1 = BuildSetupUpdateScript(setupPath, updateRoot);
        await File.WriteAllTextAsync(script, ps1, ct).ConfigureAwait(false);
        progress?.Report("Установщик готов. Приложение перезапустится.");
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

    /// <summary>Remove leftover portable/setup update files from previous runs.</summary>
    public static void CleanupStaleUpdateArtifacts()
    {
        try
        {
            var root = UpdatesRoot;
            if (!Directory.Exists(root))
                return;

            foreach (var dir in Directory.EnumerateDirectories(root, "extract-*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }

            foreach (var pattern in new[]
                     {
                         "HappAccessible-*.zip",
                         "HappAccessible-Setup-*.exe"
                     })
            {
                foreach (var file in Directory.EnumerateFiles(root, pattern))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string Ps1CleanupFunction => @"
function Remove-UpdateArtifacts {
    param(
        [string]$Root,
        [string[]]$Also
    )
    foreach ($path in $Also) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $Root) {
        Get-ChildItem -LiteralPath $Root -Filter 'extract-*' -Directory -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $Root -Filter 'HappAccessible-*.zip' -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $Root -Filter 'HappAccessible-Setup-*.exe' -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $Root -Filter 'apply-*.ps1' -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
";

    private static string BuildPortableUpdateScript(
        string targetDir,
        string sourceDir,
        string zipPath,
        string updateRoot,
        int pidToWait) =>
        Ps1CleanupFunction + $@"
$ErrorActionPreference = 'Stop'
$target = '{EscapePs1Literal(targetDir)}'
$source = '{EscapePs1Literal(sourceDir)}'
$zip = '{EscapePs1Literal(zipPath)}'
$updateRoot = '{EscapePs1Literal(updateRoot)}'
$pidToWait = {pidToWait}
while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}
Start-Sleep -Seconds 1
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
Start-Process -FilePath (Join-Path $target 'HappAccessible.exe')
Remove-UpdateArtifacts -Root $updateRoot -Also @($source, $zip, $MyInvocation.MyCommand.Path)
";

    private static string BuildSetupUpdateScript(string setupPath, string updateRoot) =>
        Ps1CleanupFunction + $@"
$ErrorActionPreference = 'Stop'
$setup = '{EscapePs1Literal(setupPath)}'
$updateRoot = '{EscapePs1Literal(updateRoot)}'
$p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART' -PassThru -Wait
if ($p.ExitCode -ne 0) {{ exit $p.ExitCode }}
Remove-UpdateArtifacts -Root $updateRoot -Also @($setup, $MyInvocation.MyCommand.Path)
";

    private static string EscapePs1Literal(string value) =>
        value.Replace("'", "''");

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
