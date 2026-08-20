using System.IO;
using System.Net.Http;

namespace HappAccessible.Services;

public static class HttpDownload
{
    /// <summary>Download URL to file with optional percent progress reports.</summary>
    public static async Task ToFileAsync(
        HttpClient http,
        string url,
        string destinationPath,
        IProgress<string>? progress,
        string label,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destinationPath);

        var buffer = new byte[81920];
        long read = 0;
        var lastPct = -1;
        while (true)
        {
            var n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is null or <= 0)
                continue;
            var pct = (int)(read * 100 / total.Value);
            if (pct == lastPct)
                continue;
            if (pct is not (0 or 100) && pct % 10 != 0)
                continue;
            lastPct = pct;
            progress?.Report($"{label}: {pct}%");
        }
    }
}
