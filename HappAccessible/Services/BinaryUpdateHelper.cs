using System.Diagnostics;
using System.IO;

namespace HappAccessible.Services;

public static class BinaryUpdateHelper
{
    public static async Task InstallExecutableAsync(
        string targetExe,
        string sourceExe,
        Func<Task>? stopRunningAsync = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceExe))
            throw new FileNotFoundException("Новый бинарник не найден.", sourceExe);

        var targetDir = Path.GetDirectoryName(targetExe)!;
        Directory.CreateDirectory(targetDir);

        if (stopRunningAsync is not null)
            await stopRunningAsync().ConfigureAwait(false);

        var backup = targetExe + ".bak";
        var staged = targetExe + ".new";

        try
        {
            if (File.Exists(staged))
                File.Delete(staged);
            File.Copy(sourceExe, staged, overwrite: true);

            await VerifyExecutableAsync(staged, ct).ConfigureAwait(false);

            if (File.Exists(targetExe))
            {
                try
                {
                    if (File.Exists(backup))
                        File.Delete(backup);
                    File.Move(targetExe, backup);
                }
                catch
                {
                    File.Delete(targetExe);
                }
            }

            File.Move(staged, targetExe);

            try
            {
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch
            {
                // keep backup if delete fails
            }
        }
        catch
        {
            try
            {
                if (File.Exists(staged))
                    File.Delete(staged);
            }
            catch
            {
                // ignore
            }

            if (!File.Exists(targetExe) && File.Exists(backup))
            {
                try { File.Move(backup, targetExe); } catch { /* ignore */ }
            }

            throw;
        }
    }

    public static void CopySidecarFiles(string sourceDir, string targetDir, params string[] names)
    {
        foreach (var name in names)
        {
            var src = Path.Combine(sourceDir, name);
            if (!File.Exists(src))
                continue;
            File.Copy(src, Path.Combine(targetDir, name), overwrite: true);
        }
    }

    private static async Task VerifyExecutableAsync(string exe, CancellationToken ct)
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
        using var process = Process.Start(psi)
                              ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(exe)}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException($"{Path.GetFileName(exe)} не ответил на version за 8 с.");
        }
    }
}
