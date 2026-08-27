using System.Diagnostics;
using System.Net.Sockets;
using HappAccessible.Models;

namespace HappAccessible.Services;

public static class PingService
{
    /// <summary>Direct TCP connect to server host:port (bypasses VPN — reachability only).</summary>
    public static async Task<int?> PingAsync(ServerProfile server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.Host) || server.Port <= 0)
            return null;

        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(server.Host, server.Port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HTTP latency through local mixed port — actual tunnel health when connected.</summary>
    public static Task<int?> PingViaTunnelAsync(int mixedPort, CancellationToken ct = default) =>
        ConnectivityProbe.ProbeHttpLatencyViaProxyAsync(mixedPort, ct);

    public static async Task PingAllAsync(
        IEnumerable<ServerProfile> servers,
        IProgress<(ServerProfile server, int? ms)>? progress = null,
        CancellationToken ct = default)
    {
        // Limited parallelism so we don't flood
        var list = servers.ToList();
        using var gate = new SemaphoreSlim(8);
        var tasks = list.Select(async s =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ms = await PingAsync(s, ct).ConfigureAwait(false);
                s.LatencyMs = ms;
                progress?.Report((s, ms));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
