using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;

namespace HappAccessible.Services;

/// <summary>
/// Runs official AmneziaWG tunnel binaries without installing the AmneziaWG UI.
/// Uses elevated <c>/installtunnelservice</c> / <c>/uninstalltunnelservice</c>.
/// </summary>
public sealed class AmneziaWgRunner
{
    public const string TunnelName = "HappAccessible";
    private const string ReleaseTagFallback = "2.0.2";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HappAccessible/" + AppUpdateService.GetCurrentVersion());
        return http;
    }

    private readonly string _toolsDir;
    private Process? _tunnelProcess;
    public string? ExePath { get; private set; }
    public string? InstalledVersion { get; private set; }

    public AmneziaWgRunner()
    {
        _toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "tools", "amneziawg");
        Directory.CreateDirectory(_toolsDir);
        InstalledVersion = CoreVersionsState.Load().AmneziaWg;
    }

    public string BundledExe => Path.Combine(_toolsDir, "amneziawg.exe");

    public bool IsTunnelRunning
    {
        get
        {
            if (TunnelServiceExists(out var status))
            {
                return status is ServiceControllerStatus.Running
                    or ServiceControllerStatus.StartPending;
            }

            // Previous interactive tunnel process, if any
            try
            {
                if (_tunnelProcess is { HasExited: false })
                    return true;
            }
            catch
            {
                // ignore
            }

            return TryReadPid() is int pid && IsProcessAlive(pid);
        }
    }

    public async Task EnsureBinaryAsync(bool forceUpdate = false, string? downloadUrl = null,
        string? expectedVersion = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        InstalledVersion = CoreVersionsState.Load().AmneziaWg;

        if (File.Exists(BundledExe) && File.Exists(Path.Combine(_toolsDir, "wintun.dll")) && !forceUpdate)
        {
            ExePath = BundledExe;
            return;
        }

        if (!forceUpdate)
        {
            foreach (var candidate in SystemInstallPaths())
            {
                if (!File.Exists(candidate))
                    continue;
                var dir = Path.GetDirectoryName(candidate)!;
                TryCopyFile(Path.Combine(dir, "amneziawg.exe"), BundledExe);
                TryCopyFile(Path.Combine(dir, "wintun.dll"), Path.Combine(_toolsDir, "wintun.dll"));
                TryCopyFile(Path.Combine(dir, "awg.exe"), Path.Combine(_toolsDir, "awg.exe"));
                if (File.Exists(BundledExe) && File.Exists(Path.Combine(_toolsDir, "wintun.dll")))
                {
                    ExePath = BundledExe;
                    return;
                }
            }
        }

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64"
        };

        string msiUrl;
        string? tag = expectedVersion;
        if (!string.IsNullOrEmpty(downloadUrl))
            msiUrl = downloadUrl;
        else
        {
            msiUrl = await ResolveMsiUrlAsync(arch, ct).ConfigureAwait(false);
            tag ??= TryExtractTagFromUrl(msiUrl);
        }

        var verLabel = CoreUpdateService.NormalizeTag(tag) ?? tag ?? "";
        var action = forceUpdate ? "Обновляю" : "Скачиваю";
        var label = string.IsNullOrEmpty(verLabel) ? "AmneziaWG" : $"AmneziaWG {verLabel}";
        progress?.Report($"{action} {label}…");

        var msiPath = Path.Combine(_toolsDir, $"amneziawg-{arch}.msi");
        await HttpDownload.ToFileAsync(Http, msiUrl, msiPath, progress, $"Загрузка {label}", ct)
            .ConfigureAwait(false);

        progress?.Report($"Распаковываю {label}…");
        var extractRoot = Path.Combine(_toolsDir, "_extract");
        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);

        var msiexec = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/a \"{msiPath}\" TARGETDIR=\"{extractRoot}\" /qn",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var p = Process.Start(msiexec)
                       ?? throw new InvalidOperationException("Не удалось запустить msiexec."))
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"msiexec распаковка MSI код {p.ExitCode}.");
        }

        var foundExe = Directory.GetFiles(extractRoot, "amneziawg.exe", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("В MSI нет amneziawg.exe.");

        var foundDir = Path.GetDirectoryName(foundExe)!;
        File.Copy(foundExe, BundledExe, overwrite: true);
        var wintun = Path.Combine(foundDir, "wintun.dll");
        if (!File.Exists(wintun))
            throw new FileNotFoundException("В MSI нет wintun.dll.");
        File.Copy(wintun, Path.Combine(_toolsDir, "wintun.dll"), overwrite: true);
        var awg = Path.Combine(foundDir, "awg.exe");
        if (File.Exists(awg))
            File.Copy(awg, Path.Combine(_toolsDir, "awg.exe"), overwrite: true);

        try
        {
            Directory.Delete(extractRoot, recursive: true);
            File.Delete(msiPath);
        }
        catch
        {
            // keep
        }

        ExePath = BundledExe;
        InstalledVersion = CoreUpdateService.NormalizeTag(tag) ?? ReleaseTagFallback;
        var state = CoreVersionsState.Load();
        state.AmneziaWg = InstalledVersion;
        state.Save();
        progress?.Report($"Готово: AmneziaWG {InstalledVersion}.");
    }

    private static string? TryExtractTagFromUrl(string url)
    {
        try
        {
            var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var i = Array.FindIndex(parts, p => p.Equals("download", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < parts.Length)
                return parts[i + 1];
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public string FindExecutable()
    {
        if (!string.IsNullOrEmpty(ExePath) && File.Exists(ExePath))
            return ExePath;
        if (File.Exists(BundledExe))
        {
            ExePath = BundledExe;
            return BundledExe;
        }

        foreach (var candidate in SystemInstallPaths())
        {
            if (File.Exists(candidate))
            {
                ExePath = candidate;
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Движок AmneziaWG ещё не скачан. Подключите AWG-сервер — клиент загрузит его сам.");
    }

    public async Task ConnectAsync(string sourceConfPath, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureBinaryAsync(progress: progress, ct: ct).ConfigureAwait(false);
        var exe = FindExecutable();

        await DisconnectAsync(ct).ConfigureAwait(false);

        AmneziaWgConfigStore.PrepareActiveConfig(sourceConfPath);
        var active = AmneziaWgConfigStore.ActiveConfigPath;

        progress?.Report("Запуск туннеля AmneziaWG (подтвердите UAC)…");

        int code;
        try
        {
            code = await RunElevatedAsync(exe, $"/installtunnelservice \"{active}\"", ct)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("UAC отклонён — без прав администратора туннель не поднять.");
        }

        if (code != 0)
            throw new InvalidOperationException(
                $"AmneziaWG installtunnelservice код {code}. Проверьте .conf и права админа.");

        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (IsTunnelRunning)
                return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        if (!IsTunnelRunning)
            throw new InvalidOperationException(
                "Служба AmneziaWGTunnel$HappAccessible не запустилась. Проверьте конфиг.");
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        // Stop orphaned tunnel processes
        var pids = new HashSet<int>();
        try
        {
            if (_tunnelProcess is { HasExited: false })
                pids.Add(_tunnelProcess.Id);
        }
        catch
        {
            // ignore
        }

        if (TryReadPid() is int saved)
            pids.Add(saved);

        foreach (var pid in pids)
            await KillPidElevatedAsync(pid, ct).ConfigureAwait(false);

        try { _tunnelProcess?.Dispose(); } catch { /* ignore */ }
        _tunnelProcess = null;
        ClearPidFile();

        // Uninstall tunnel service only when it is present
        if (TunnelServiceExists(out _))
        {
            try
            {
                var exe = FindExecutable();
                await RunElevatedAsync(exe, $"/uninstalltunnelservice {TunnelName}", ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await Task.Delay(300, ct).ConfigureAwait(false);
    }

    private static bool TunnelServiceExists(out ServiceControllerStatus status)
    {
        status = default;
        try
        {
            using var sc = new ServiceController("AmneziaWGTunnel$" + TunnelName);
            status = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if <c>awg show</c> reports a recent handshake (tunnel is actually talking to peer).</summary>
    public async Task<bool> HasHandshakeAsync(CancellationToken ct = default)
    {
        var awg = Path.Combine(_toolsDir, "awg.exe");
        if (!File.Exists(awg))
        {
            // fall back to system install next to amneziawg
            try
            {
                var exe = FindExecutable();
                var dir = Path.GetDirectoryName(exe)!;
                var candidate = Path.Combine(dir, "awg.exe");
                if (File.Exists(candidate))
                    awg = candidate;
                else
                    return false;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = awg,
                Arguments = $"show {TunnelName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            // "latest handshake: 3 seconds ago" / transfer counters mean peer is alive
            var hasHandshakeLine = output.Contains("latest handshake", StringComparison.OrdinalIgnoreCase);
            var never = output.Contains("latest handshake: 0 seconds", StringComparison.OrdinalIgnoreCase);
            var hasTransfer = output.Contains("transfer:", StringComparison.OrdinalIgnoreCase)
                              && output.Contains("received", StringComparison.OrdinalIgnoreCase);
            return (hasHandshakeLine && !never) || hasTransfer;
        }
        catch
        {
            return false;
        }
    }

    private static async Task KillPidElevatedAsync(int pid, CancellationToken ct)
    {
        if (pid <= 0 || !IsProcessAlive(pid))
            return;

        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return;
        }
        catch
        {
            // Likely elevated — use taskkill via UAC
        }

        try
        {
            await RunElevatedAsync("taskkill.exe", $"/F /T /PID {pid}", ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<int> RunElevatedAsync(string fileName, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var p = Process.Start(psi)
                      ?? throw new InvalidOperationException("Не удалось запустить elevated-процесс.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    private static int? TryReadPid()
    {
        try
        {
            var path = AmneziaWgConfigStore.PidFilePath;
            if (!File.Exists(path))
                return null;
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearPidFile()
    {
        try
        {
            var path = AmneziaWgConfigStore.PidFilePath;
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ResolveMsiUrlAsync(string arch, CancellationToken ct)
    {
        var pinned =
            $"https://github.com/amnezia-vpn/amneziawg-windows-client/releases/download/{ReleaseTagFallback}/amneziawg-{arch}-{ReleaseTagFallback}.msi";
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, pinned);
            using var resp = await Http.SendAsync(head, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return pinned;
        }
        catch
        {
            // fall through
        }

        using var release = await Http.GetAsync(
            "https://api.github.com/repos/amnezia-vpn/amneziawg-windows-client/releases/latest", ct)
            .ConfigureAwait(false);
        release.EnsureSuccessStatusCode();
        await using var stream = await release.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains($"amneziawg-{arch}-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("windows7", StringComparison.OrdinalIgnoreCase))
            {
                return asset.GetProperty("browser_download_url").GetString()
                       ?? throw new InvalidOperationException("Пустой URL MSI.");
            }
        }

        throw new InvalidOperationException($"Не найден MSI AmneziaWG для {arch}.");
    }

    private static IEnumerable<string> SystemInstallPaths()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf, "AmneziaWG", "amneziawg.exe");
        yield return Path.Combine(pf86, "AmneziaWG", "amneziawg.exe");
    }

    private static void TryCopyFile(string src, string dst)
    {
        try
        {
            if (File.Exists(src))
                File.Copy(src, dst, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }
}
