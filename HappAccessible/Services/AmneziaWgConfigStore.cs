using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class AmneziaWgConfig
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public string? EndpointHost { get; init; }
    public int EndpointPort { get; init; }
    public bool HasObfuscation { get; init; }

    public ServerProfile ToProfile() => new()
    {
        Name = Name,
        Protocol = "amneziawg",
        RawUri = "awg://" + FilePath,
        Host = EndpointHost,
        Port = EndpointPort,
        IsWhitelistBypass = false
    };
}

public static class AmneziaWgConfigStore
{
    /// <summary>Imported configs (user-readable).</summary>
    public static string AwgDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HappAccessible", "awg");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Active tunnel config must live in ProgramData so the elevated AmneziaWG process / service can read it.
    /// </summary>
    public static string RuntimeDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HappAccessible", "awg");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Active tunnel always uses this file name.</summary>
    public static string ActiveTunnelName => "HappAccessible";

    public static string ActiveConfigPath => Path.Combine(RuntimeDir, ActiveTunnelName + ".conf");

    public static string PidFilePath => Path.Combine(RuntimeDir, "tunnel.pid");

    public static bool LooksLikeConf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return Regex.IsMatch(text, @"^\s*\[Interface\]", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    public static AmneziaWgConfig ImportFromText(string confText, string preferredName)
    {
        if (!LooksLikeConf(confText))
            throw new InvalidOperationException("Это не AmneziaWG/WireGuard .conf ([Interface] не найден).");

        var safe = SanitizeFileName(preferredName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "imported-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var path = Path.Combine(AwgDir, safe + ".conf");
        // avoid clobbering active tunnel file name used at connect time
        if (string.Equals(safe, ActiveTunnelName, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(AwgDir, safe + "-cfg.conf");

        File.WriteAllText(path, NormalizeConf(confText), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return ParseFile(path);
    }

    public static AmneziaWgConfig ImportFromFile(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return ImportFromText(text, name);
    }

    public static IReadOnlyList<AmneziaWgConfig> ListImported()
    {
        var list = new List<AmneziaWgConfig>();
        foreach (var file in Directory.GetFiles(AwgDir, "*.conf"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, ActiveTunnelName, StringComparison.OrdinalIgnoreCase))
                continue; // runtime copy only
            try
            {
                list.Add(ParseFile(file));
            }
            catch
            {
                // skip broken
            }
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static AmneziaWgConfig ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);
        string? endpoint = null;
        var hasObf = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                endpoint = val;
            if (Regex.IsMatch(key, @"^(Jc|Jmin|Jmax|S1|S2|S3|S4|H1|H2|H3|H4|I1|I2|I3|I4|I5)$",
                    RegexOptions.IgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(val) && val != "0")
                    hasObf = true;
            }
        }

        string? host = null;
        var port = 0;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var ep = endpoint.Trim();
            if (ep.StartsWith('['))
            {
                var end = ep.IndexOf(']');
                host = ep[1..end];
                var rest = ep[(end + 1)..].TrimStart(':');
                _ = int.TryParse(rest, out port);
            }
            else
            {
                var idx = ep.LastIndexOf(':');
                if (idx > 0)
                {
                    host = ep[..idx];
                    _ = int.TryParse(ep[(idx + 1)..], out port);
                }
                else host = ep;
            }
        }

        return new AmneziaWgConfig
        {
            Name = (hasObf ? "AWG " : "WG ") + name,
            FilePath = path,
            EndpointHost = host,
            EndpointPort = port,
            HasObfuscation = hasObf
        };
    }

    public static void PrepareActiveConfig(string sourceConfPath)
    {
        var text = File.ReadAllText(sourceConfPath);
        File.WriteAllText(ActiveConfigPath, NormalizeConf(text),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string? TryResolveSourcePath(ServerProfile server)
    {
        if (!string.Equals(server.Protocol, "amneziawg", StringComparison.OrdinalIgnoreCase))
            return null;
        if (server.RawUri.StartsWith("awg://", StringComparison.OrdinalIgnoreCase))
        {
            var path = server.RawUri["awg://".Length..];
            return File.Exists(path) ? path : null;
        }

        return File.Exists(server.RawUri) ? server.RawUri : null;
    }

    private static string NormalizeConf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";

    private static string SanitizeFileName(string name)
    {
        var s = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        s = Regex.Replace(s, @"\s+", "-");
        return s.Length > 60 ? s[..60] : s;
    }
}
