using System.Collections.Concurrent;
using System.IO;

namespace HappAccessible.Services;

public static class SessionJournalService
{
    private const int MaxEntries = 20;
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly object FileLock = new();

    private static string JournalPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "session-journal.log");

    public static void Record(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Recent.Enqueue(line);
        while (Recent.Count > MaxEntries && Recent.TryDequeue(out _))
        {
            // trim
        }

        AppLogService.Info("[journal] " + message);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                File.AppendAllText(JournalPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static IReadOnlyList<string> GetRecent() => Recent.ToArray();

    public static string FormatRecentForDisplay()
    {
        var items = GetRecent();
        return items.Count == 0
            ? "Журнал сессии пуст."
            : string.Join(Environment.NewLine, items);
    }
}
