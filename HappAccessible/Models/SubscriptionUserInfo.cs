namespace HappAccessible.Models;

/// <summary>
/// Parsed from HTTP <c>subscription-userinfo</c> / <c>Subscription-Userinfo</c> response headers.
/// Format: upload=…; download=…; total=…; expire=…
/// </summary>
public sealed class SubscriptionUserInfo
{
    public long UploadBytes { get; init; }
    public long DownloadBytes { get; init; }
    public long TotalBytes { get; init; }
    public long ExpireUnix { get; init; }

    public long UsedBytes => Math.Max(0, UploadBytes + DownloadBytes);

    public long? RemainingBytes =>
        TotalBytes > 0 ? Math.Max(0, TotalBytes - UsedBytes) : null;

    public DateTimeOffset? ExpireUtc =>
        ExpireUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(ExpireUnix)
            : null;

    public static SubscriptionUserInfo? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        long upload = 0, download = 0, total = 0, expire = 0;
        var any = false;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var val = part[(eq + 1)..].Trim();
            if (!long.TryParse(val, out var n))
                continue;
            any = true;
            switch (key)
            {
                case "upload": upload = n; break;
                case "download": download = n; break;
                case "total": total = n; break;
                case "expire": expire = n; break;
            }
        }

        return any
            ? new SubscriptionUserInfo
            {
                UploadBytes = upload,
                DownloadBytes = download,
                TotalBytes = total,
                ExpireUnix = expire
            }
            : null;
    }
}
