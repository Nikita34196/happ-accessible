using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class SingBoxRunner : IDisposable
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HappAccessible/" + AppUpdateService.GetCurrentVersion());
        return http;
    }

    private Process? _process;
    private readonly StringBuilder _log = new();
    private readonly string _toolsDir;
    private readonly string _dataDir;

    public SingBoxRunner()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible");
        _toolsDir = Path.Combine(root, "tools");
        _dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(_toolsDir);
        Directory.CreateDirectory(_dataDir);
    }

    public string ConfigPath => Path.Combine(_dataDir, "config.json");
    public string LogPath => Path.Combine(_dataDir, "sing-box.log");
    public bool IsRunning => _process is { HasExited: false };

    public event Action? CoreExited;

    public string RecentLog
    {
        get
        {
            lock (_log)
            {
                var s = _log.ToString();
                return s.Length <= 800 ? s : s[^800..];
            }
        }
    }

    private int _activeMixedPort = EngineOptions.DefaultMixedPort;
    public int ActiveMixedPort => _activeMixedPort;

    public string? CoreVersion { get; private set; }

    public async Task EnsureBinaryAsync(bool forceUpdate = false, string? downloadUrl = null,
        string? expectedVersion = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var exe = Path.Combine(_toolsDir, "sing-box.exe");
        if (File.Exists(exe) && !forceUpdate)
        {
            await TryReadCoreVersionAsync(exe, ct).ConfigureAwait(false);
            // Trust the real binary only — core-versions.json can lie after a failed replace
            if (CoreUpdateService.IsLxBuild(CoreVersion))
                return;
            forceUpdate = true;
            progress?.Report(
                $"Найден stock sing-box ({CoreVersion ?? "—"}) без xhttp — ставлю sing-box-lx…");
        }

        if (_process is { HasExited: false })
            await StopAsync().ConfigureAwait(false);

        string zipUrl;
        string? tag = expectedVersion;
        if (!string.IsNullOrEmpty(downloadUrl))
        {
            zipUrl = downloadUrl;
        }
        else
        {
            using var releaseResponse = await Http.GetAsync(
                "https://api.github.com/repos/Leadaxe/sing-box-lx/releases/latest", ct).ConfigureAwait(false);
            releaseResponse.EnsureSuccessStatusCode();
            await using var stream = await releaseResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            tag = doc.RootElement.GetProperty("tag_name").GetString();
            zipUrl = null!;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("windows-amd64", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("legacy", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString()!;
                    break;
                }
            }

            if (string.IsNullOrEmpty(zipUrl))
                throw new InvalidOperationException("Не найден sing-box-lx windows-amd64 в релизе.");
        }

        var verLabel = CoreUpdateService.NormalizeTag(tag) ?? tag ?? "";
        var action = forceUpdate ? "Обновляю" : "Скачиваю";
        var label = string.IsNullOrEmpty(verLabel) ? "sing-box-lx" : $"sing-box-lx {verLabel}";
        progress?.Report($"{action} {label}…");

        var zipPath = Path.Combine(_toolsDir, "sing-box.zip");
        var extractDir = Path.Combine(_toolsDir, "_extract-sing-box");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        await HttpDownload.ToFileAsync(Http, zipUrl, zipPath, progress, $"Загрузка {label}", ct)
            .ConfigureAwait(false);

        progress?.Report($"Распаковываю {label}…");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { /* ignore */ }

        var found = Directory.GetFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories)
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault()
                    ?? throw new FileNotFoundException("sing-box.exe не найден после распаковки lx.");

        var foundDir = Path.GetDirectoryName(found)!;

        await BinaryUpdateHelper.InstallExecutableAsync(
            exe,
            found,
            stopRunningAsync: StopAsync,
            ct: ct).ConfigureAwait(false);
        BinaryUpdateHelper.CopySidecarFiles(foundDir, _toolsDir, "libcronet.dll", "wintun.dll");

        try { Directory.Delete(extractDir, recursive: true); } catch { /* keep */ }

        await TryReadCoreVersionAsync(exe, ct).ConfigureAwait(false);
        var saved = CoreUpdateService.IsLxBuild(CoreVersion)
            ? CoreUpdateService.NormalizeTag(CoreVersion)
            : CoreUpdateService.NormalizeTag(tag);

        if (!CoreUpdateService.IsLxBuild(saved))
            throw new InvalidOperationException(
                $"Установка sing-box-lx не подтверждена (version={CoreVersion}, tag={tag}).");

        var state = CoreVersionsState.Load();
        state.SingBox = saved;
        state.Save();
        progress?.Report($"Готово: sing-box {state.SingBox}.");
    }

    private async Task TryReadCoreVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
                CoreVersion = CoreUpdateService.NormalizeTag(line) ?? line.Trim();
        }
        catch
        {
            // ignore
        }
    }

    public async Task StartAsync(ServerProfile server, bool tun, RoutingOptions? routing = null,
        EngineOptions? engine = null, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        await EnsureBinaryAsync(ct: ct).ConfigureAwait(false);

        engine ??= new EngineOptions();
        _activeMixedPort = EngineOptions.ClampPort(engine.MixedPort);

        lock (_log) _log.Clear();

        var json = SingBoxConfigBuilder.Build(server, tun, routing, engine);
        // Keep a stable path + a timestamped copy for debugging (like Vireo temp configs)
        await File.WriteAllTextAsync(ConfigPath, json, ct).ConfigureAwait(false);
        try
        {
            var stamp = Path.Combine(_dataDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(stamp, json, ct).ConfigureAwait(false);
            // Keep only a few recent dumps
            foreach (var old in Directory.GetFiles(_dataDir, "config-*.json")
                         .OrderByDescending(f => f).Skip(5))
            {
                try { File.Delete(old); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore dump failures
        }

        var exe = Path.Combine(_toolsDir, "sing-box.exe");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"run -c \"{ConfigPath}\"",
            WorkingDirectory = _toolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Safety net for older configs / transitional sing-box versions
        psi.Environment["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "true";
        psi.Environment["ENABLE_DEPRECATED_SPECIAL_OUTBOUNDS"] = "true";
        psi.Environment["ENABLE_DEPRECATED_MISSING_DISCOVERY_STRATEGY"] = "true";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _process.Exited += (_, _) => CoreExited?.Invoke();

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить sing-box.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Give core a moment to bind / fail fast
        await ConnectivityProbe.WaitForProcessReadyAsync(_process, TimeSpan.FromSeconds(2), ct)
            .ConfigureAwait(false);
        if (_process.HasExited)
        {
            var code = _process.ExitCode;
            throw new InvalidOperationException(
                $"sing-box сразу завершился (код {code}). {RecentLog}");
        }
    }

    public async Task<(bool Ok, string Detail)> ProbeSessionHealthAsync(CancellationToken ct = default) =>
        await ConnectivityProbe.ProbeSessionHealthAsync(_activeMixedPort, ct).ConfigureAwait(false);

    public async Task<bool> ProbeConnectivityAsync(CancellationToken ct = default) =>
        await ConnectivityProbe.ProbeHttpViaProxyAsync(_activeMixedPort, ct).ConfigureAwait(false);

    public async Task StopAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process.Dispose();
            _process = null;
            try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch { /* best effort */ }
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 20_000)
                _log.Remove(0, _log.Length - 10_000);
        }

        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
