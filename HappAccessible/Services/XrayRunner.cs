using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class XrayRunner : IDisposable
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp() =>
        DirectHttp.Create(TimeSpan.FromMinutes(5), "HappAccessible/" + AppUpdateService.GetCurrentVersion());

    private Process? _process;
    private readonly StringBuilder _log = new();
    private readonly string _toolsDir;
    private readonly string _dataDir;
    private int _activePort = EngineOptions.DefaultMixedPort;

    public XrayRunner()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible");
        _toolsDir = Path.Combine(root, "tools", "xray");
        _dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(_toolsDir);
        Directory.CreateDirectory(_dataDir);
    }

    public string ExePath => Path.Combine(_toolsDir, "xray.exe");
    public string ConfigPath => Path.Combine(_dataDir, "xray-config.json");
    public string? CoreVersion { get; private set; }
    public int ActivePort => _activePort;
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

    public async Task EnsureBinaryAsync(bool forceUpdate = false, string? downloadUrl = null,
        string? expectedVersion = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(ExePath) && !forceUpdate)
        {
            await TryReadVersionAsync(ct).ConfigureAwait(false);
            return;
        }

        string zipUrl;
        string? tag = expectedVersion;
        if (!string.IsNullOrEmpty(downloadUrl))
        {
            zipUrl = downloadUrl;
        }
        else
        {
            using var releaseResponse = await Http.GetAsync(
                "https://api.github.com/repos/XTLS/Xray-core/releases/latest", ct).ConfigureAwait(false);
            releaseResponse.EnsureSuccessStatusCode();
            await using var stream = await releaseResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            tag = doc.RootElement.GetProperty("tag_name").GetString();
            zipUrl = null!;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Equals("Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString()!;
                    break;
                }
            }

            if (string.IsNullOrEmpty(zipUrl))
                throw new InvalidOperationException("Не найден Xray-windows-64.zip в релизе.");
        }

        var zipPath = Path.Combine(_toolsDir, "xray.zip");
        if (_process is { HasExited: false })
            await StopAsync().ConfigureAwait(false);

        var verLabel = CoreUpdateService.NormalizeTag(tag) ?? tag ?? "";
        var action = forceUpdate ? "Обновляю" : "Скачиваю";
        var label = string.IsNullOrEmpty(verLabel) ? "Xray" : $"Xray {verLabel}";
        progress?.Report($"{action} {label}…");

        await HttpDownload.ToFileAsync(Http, zipUrl, zipPath, progress, $"Загрузка {label}", ct)
            .ConfigureAwait(false);

        progress?.Report($"Распаковываю {label}…");
        var extract = Path.Combine(_toolsDir, "_extract");
        if (Directory.Exists(extract))
            Directory.Delete(extract, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);
        File.Delete(zipPath);

        var found = Directory.GetFiles(extract, "xray.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new FileNotFoundException("xray.exe не найден в архиве.");
        await BinaryUpdateHelper.InstallExecutableAsync(
            ExePath,
            found,
            stopRunningAsync: StopAsync,
            ct: ct).ConfigureAwait(false);
        foreach (var dat in new[] { "geoip.dat", "geosite.dat" })
        {
            var src = Directory.GetFiles(extract, dat, SearchOption.AllDirectories).FirstOrDefault();
            if (src is not null)
                File.Copy(src, Path.Combine(_toolsDir, dat), overwrite: true);
        }

        try { Directory.Delete(extract, recursive: true); } catch { /* ignore */ }

        await TryReadVersionAsync(ct).ConfigureAwait(false);
        var state = CoreVersionsState.Load();
        state.Xray = CoreUpdateService.NormalizeTag(tag) ?? CoreUpdateService.NormalizeTag(CoreVersion);
        state.Save();
        progress?.Report($"Готово: Xray {state.Xray}.");
    }

    private async Task TryReadVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "version",
                WorkingDirectory = _toolsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            // "Xray 26.7.28 (Xray, Penetrates Everything.) ..."
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                CoreVersion = parts[1];
            else
                CoreVersion = line;
        }
        catch
        {
            // ignore
        }
    }

    public async Task StartAsync(ServerProfile server, EngineOptions? engine = null, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        await EnsureBinaryAsync(ct: ct).ConfigureAwait(false);

        engine ??= new EngineOptions();
        _activePort = EngineOptions.ClampPort(engine.MixedPort);

        lock (_log) _log.Clear();
        ResetLogFile();
        var json = XrayConfigBuilder.Build(server, _activePort);
        await File.WriteAllTextAsync(ConfigPath, json, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"run -c \"{ConfigPath}\"",
            WorkingDirectory = _toolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _process.Exited += (_, _) => CoreExited?.Invoke();

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить Xray.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await ConnectivityProbe.WaitForProcessReadyAsync(_process, TimeSpan.FromSeconds(1), ct)
            .ConfigureAwait(false);
        var ready = await ConnectivityProbe.WaitForMixedPortReadyAsync(
                _activePort,
                _process,
                TimeSpan.FromSeconds(6),
                ct)
            .ConfigureAwait(false);
        if (_process.HasExited)
            throw new InvalidOperationException($"Xray сразу завершился (код {_process.ExitCode}). {RecentLog}");
        if (!ready)
            AppLogService.Warn("Xray запущен, но HTTP-порт пока не отвечает.");
    }

    public async Task<(bool Ok, string Detail)> ProbeSessionHealthAsync(CancellationToken ct = default) =>
        await ConnectivityProbe.ProbeSessionHealthAsync(_activePort, ct).ConfigureAwait(false);

    public async Task<bool> ProbeConnectivityAsync(CancellationToken ct = default) =>
        await ConnectivityProbe.ProbeHttpViaProxyAsync(
            _activePort,
            ct,
            attempts: 4,
            retryDelay: TimeSpan.FromSeconds(2)).ConfigureAwait(false);

    public async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            process.Dispose();
            try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch { /* best effort */ }
        }
    }

    private void ResetLogFile()
    {
        try
        {
            File.WriteAllText(
                LogPath,
                $"# Xray log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must never interfere with starting the tunnel.
        }
    }

    public string LogPath => Path.Combine(_toolsDir, "xray.log");

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

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
