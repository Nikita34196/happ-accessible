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
/// Checks GitHub Releases for Happ Accessible and applies portable updates.
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

    /// <summary>
    /// Downloads the update and prepares a script that replaces files after the app exits.
    /// Returns path to the updater script (caller should exit the app after starting it).
    /// </summary>
    public async Task<string> PreparePortableUpdateAsync(
        AppReleaseInfo info,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.PortableZipUrl))
            throw new InvalidOperationException("В релизе нет portable zip.");

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "updates");
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

        // If zip has a single top-level folder, use it
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
        var script = Path.Combine(updateRoot, "apply-update.cmd");
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            $"set TARGET={QuoteCmd(targetDir)}",
            $"set SOURCE={QuoteCmd(payload)}",
            $"set PID={pid}",
            "echo Waiting for Happ Accessible to exit...",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "timeout /t 1 /nobreak >nul",
            "echo Copying files...",
            "xcopy \"%SOURCE%\\*\" \"%TARGET%\\\" /E /Y /Q /I >nul",
            "if errorlevel 1 (",
            "  echo Update failed.",
            "  pause",
            "  exit /b 1",
            ")",
            "echo Starting Happ Accessible...",
            "start \"\" \"%TARGET%\\HappAccessible.exe\"",
            "exit /b 0"
        };
        await File.WriteAllLinesAsync(script, lines, ct).ConfigureAwait(false);
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

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "updates");
        Directory.CreateDirectory(updateRoot);
        var setupPath = Path.Combine(updateRoot, $"HappAccessible-Setup-{info.LatestVersion}.exe");
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
            FileName = scriptPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        });
    }

    public static void LaunchSetup(string setupPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = true
        });
    }

    private static string QuoteCmd(string path) => path.Replace("\"", "");
}
