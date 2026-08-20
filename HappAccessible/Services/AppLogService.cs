using System.Diagnostics;
using System.IO;
using System.Text;

namespace HappAccessible.Services;

/// <summary>Application error/info log under %LocalAppData%\HappAccessible\logs.</summary>
public static class AppLogService
{
    private static readonly object Gate = new();

    public static string LogsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappAccessible", "logs");

    public static string LogPath => Path.Combine(LogsDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static string EnsureLogFile()
    {
        Directory.CreateDirectory(LogsDirectory);
        lock (Gate)
        {
            if (!File.Exists(LogPath))
            {
                File.WriteAllText(LogPath,
                    $"# Happ Accessible log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }

        return LogPath;
    }

    /// <summary>Opens Explorer with the log file selected.</summary>
    public static void OpenInExplorer()
    {
        var path = EnsureLogFile();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LogsDirectory,
                UseShellExecute = true
            });
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(level).Append("] ");
            sb.Append(message);
            if (ex is not null)
            {
                sb.AppendLine();
                sb.Append(ex);
            }

            sb.AppendLine();
            lock (Gate)
            {
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                TryTrim(LogPath);
            }
        }
        catch
        {
            // never throw from logger
        }
    }

    private static void TryTrim(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length < 2_000_000)
                return;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 400)
                return;
            File.WriteAllLines(path, lines.Skip(lines.Length / 2));
        }
        catch
        {
            // ignore
        }
    }
}
