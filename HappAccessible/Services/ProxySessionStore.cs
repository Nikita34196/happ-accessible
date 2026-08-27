using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappAccessible.Services;

/// <summary>Persists proxy ownership so crash recovery only touches our own session.</summary>
public sealed class ProxySessionStore
{
    private const string Marker = "HappAccessible/1";

    public sealed class Session
    {
        public string Marker { get; set; } = "HappAccessible/1";
        public string ProxyServer { get; set; } = "";
        public int? PrevEnable { get; set; }
        public string? PrevServer { get; set; }
        public string? PrevOverride { get; set; }
        public bool HadOverride { get; set; }
        public DateTimeOffset SavedUtc { get; set; }
    }

    private static string SessionPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible",
            "proxy-session.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Save(Session session)
    {
        try
        {
            session.Marker = Marker;
            session.SavedUtc = DateTimeOffset.UtcNow;
            var dir = Path.GetDirectoryName(SessionPath)!;
            Directory.CreateDirectory(dir);
            var tmp = SessionPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
            File.Copy(tmp, SessionPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    public static Session? TryLoad()
    {
        try
        {
            if (!File.Exists(SessionPath))
                return null;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(SessionPath), JsonOptions);
            if (session is null || !string.Equals(session.Marker, Marker, StringComparison.Ordinal))
                return null;
            return session;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(SessionPath))
                File.Delete(SessionPath);
        }
        catch
        {
            // ignore
        }
    }
}
