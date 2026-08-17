using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace HappAccessible.Services;

public static class ElevationHelper
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Starts a new elevated copy of this exe. Returns false if UAC was cancelled.
    /// Caller should shut down the current process on success.
    /// </summary>
    public static bool TryRelaunchElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exe))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? "",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined
            return false;
        }
        catch
        {
            return false;
        }
    }
}
