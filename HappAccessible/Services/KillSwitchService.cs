using System.Diagnostics;
using System.IO;

namespace HappAccessible.Services;

/// <summary>Blocks outbound traffic when VPN session drops unexpectedly (requires admin).</summary>
public sealed class KillSwitchService
{
    private const string RulePrefix = "HappAccessible-KillSwitch";
    private bool _armed;

    public bool IsArmed => _armed;

    public void Arm(string? coreExePath)
    {
        if (!ElevationHelper.IsElevated)
            return;

        try
        {
            RemoveRules();
            RunNetsh($"advfirewall firewall add rule name=\"{RulePrefix}-Block\" dir=out action=block enable=yes");
            RunNetsh($"advfirewall firewall add rule name=\"{RulePrefix}-Loopback\" dir=out action=allow remoteip=127.0.0.0/8 enable=yes");
            RunNetsh($"advfirewall firewall add rule name=\"{RulePrefix}-Local\" dir=out action=allow remoteip=169.254.0.0/16 enable=yes");
            if (!string.IsNullOrWhiteSpace(coreExePath) && File.Exists(coreExePath))
            {
                var exe = coreExePath.Replace("\"", "");
                RunNetsh($"advfirewall firewall add rule name=\"{RulePrefix}-Core\" dir=out action=allow program=\"{exe}\" enable=yes");
            }

            _armed = true;
            SessionJournalService.Record("Kill switch включён.");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Kill switch: не удалось включить", ex);
        }
    }

    public void Disarm()
    {
        if (!_armed && !ElevationHelper.IsElevated)
            return;

        try
        {
            RemoveRules();
            _armed = false;
            SessionJournalService.Record("Kill switch выключён.");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Kill switch: не удалось выключить", ex);
        }
    }

    private static void RemoveRules()
    {
        foreach (var suffix in new[] { "-Block", "-Loopback", "-Local", "-Core" })
        {
            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}{suffix}\"");
            }
            catch
            {
                // ignore missing rule
            }
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh не запустился.");
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"netsh exit {p.ExitCode}: {err}");
        }
    }
}
