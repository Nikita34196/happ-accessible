using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

public sealed class IncyCryptScheme
{
    public string Host { get; set; } = "crypt1";
    public string Prefix { get; set; } = "incy://crypt1/";
    public string Fingerprint { get; set; } = "";
    public string Salt { get; set; } = "";
    public int KeymatAOffset { get; set; } = 1024;
    public int KeymatBOffset { get; set; } = 2048;
    public string KeyHex { get; set; } = "";
}

public sealed class IncyCompatState
{
    public string? UserAgent { get; set; }
    public string? DesktopVersion { get; set; }
    public DateTimeOffset? CheckedUtc { get; set; }
    public List<IncyCryptScheme> Schemes { get; set; } = [];
}

public static class IncyCompatStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string PathFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "incy-compat.json");

    public static IncyCompatState Load()
    {
        try
        {
            if (!File.Exists(PathFile))
                return new IncyCompatState();
            return JsonSerializer.Deserialize<IncyCompatState>(File.ReadAllText(PathFile), JsonOptions)
                   ?? new IncyCompatState();
        }
        catch
        {
            return new IncyCompatState();
        }
    }

    public static void Save(IncyCompatState state)
    {
        var dir = Path.GetDirectoryName(PathFile)!;
        Directory.CreateDirectory(dir);
        var tmp = PathFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, PathFile, overwrite: true);
    }

    public static string UserAgentOrFallback(string fallback)
    {
        var ua = Load().UserAgent?.Trim();
        return string.IsNullOrWhiteSpace(ua) ? fallback : ua;
    }
}
