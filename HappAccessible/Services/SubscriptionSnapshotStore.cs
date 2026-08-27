using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class SubscriptionSnapshotStore
{
    private sealed class Snapshot
    {
        public string SubscriptionKey { get; set; } = "";
        public DateTimeOffset SavedUtc { get; set; }
        public List<ServerSnapshot> Servers { get; set; } = [];
    }

    private sealed class ServerSnapshot
    {
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string RawUri { get; set; } = "";
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? OriginalName { get; set; }
        public bool IsWhitelistBypass { get; set; }
    }

    private static string SnapshotPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible",
            "last-servers.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Save(string subscriptionKey, IEnumerable<ServerProfile> servers)
    {
        try
        {
            var snap = new Snapshot
            {
                SubscriptionKey = subscriptionKey,
                SavedUtc = DateTimeOffset.UtcNow,
                Servers = servers.Select(s => new ServerSnapshot
                {
                    Name = s.Name,
                    Protocol = s.Protocol,
                    RawUri = s.RawUri,
                    Host = s.Host,
                    Port = s.Port,
                    OriginalName = s.OriginalName,
                    IsWhitelistBypass = s.IsWhitelistBypass
                }).ToList()
            };
            var dir = Path.GetDirectoryName(SnapshotPath)!;
            Directory.CreateDirectory(dir);
            var tmp = SnapshotPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, JsonOptions));
            File.Copy(tmp, SnapshotPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    public static IReadOnlyList<ServerProfile>? TryLoad(string subscriptionKey)
    {
        try
        {
            if (!File.Exists(SnapshotPath))
                return null;
            var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath), JsonOptions);
            if (snap is null || snap.Servers.Count == 0)
                return null;
            if (!string.Equals(snap.SubscriptionKey, subscriptionKey, StringComparison.Ordinal))
                return null;
            return snap.Servers.Select(s => new ServerProfile
            {
                Name = s.Name,
                Protocol = s.Protocol,
                RawUri = s.RawUri,
                Host = s.Host,
                Port = s.Port,
                OriginalName = s.OriginalName,
                IsWhitelistBypass = s.IsWhitelistBypass
            }).ToList();
        }
        catch
        {
            return null;
        }
    }
}
