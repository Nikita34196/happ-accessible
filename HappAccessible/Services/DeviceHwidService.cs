using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HappAccessible.Services;

public static class DeviceHwidService
{
    public static string EnsureHwid(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DeviceHwid)
            && settings.DeviceHwid.Length is >= 10 and <= 64)
            return settings.DeviceHwid;

        settings.DeviceHwid = CreateStableHwid();
        settings.Save();
        return settings.DeviceHwid;
    }

    private static string CreateStableHwid()
    {
        var seed = new StringBuilder();
        seed.Append(Environment.MachineName);
        seed.Append('|');
        seed.Append(Environment.UserName);
        seed.Append('|');
        seed.Append(GetMachineGuid() ?? "no-guid");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        var b64 = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return b64.Length >= 12 ? b64[..24] : b64.PadRight(12, 'A');
    }

    private static string? GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
