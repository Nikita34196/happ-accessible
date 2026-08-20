using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using HappAccessible.Models;

namespace HappAccessible.Services;

/// <summary>Fast TCP preflight and HTTP tunnel probes shared by cores.</summary>
public static class ConnectivityProbe
{
    public static async Task<(bool Ok, string Detail)> PreflightTcpAsync(
        ServerProfile server, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.Equals(server.Protocol, "amneziawg", StringComparison.OrdinalIgnoreCase))
            return (true, "skip");

        if (string.IsNullOrWhiteSpace(server.Host) || server.Port <= 0)
            return (true, "no-host");

        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await client.ConnectAsync(server.Host, server.Port, linked.Token).ConfigureAwait(false);
            sw.Stop();
            return (true, $"{sw.ElapsedMilliseconds} мс");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"таймаут TCP {server.Host}:{server.Port}");
        }
        catch (Exception ex)
        {
            return (false, $"TCP {server.Host}:{server.Port}: {ex.Message}");
        }
    }

    public static async Task WaitForProcessReadyAsync(
        Process process, TimeSpan maxWait, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                return;
            // Still alive after ~350ms — treat as ready (fail-fast if it dies later)
            if (sw.ElapsedMilliseconds >= 350)
                return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public static async Task<bool> ProbeHttpViaProxyAsync(
        int mixedPort, CancellationToken ct = default)
    {
        var urls = new[]
        {
            "http://www.gstatic.com/generate_204",
            "http://connectivitycheck.gstatic.com/generate_204",
            "http://cp.cloudflare.com/"
        };

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(5));
                using var resp = await client.GetAsync(url, linked.Token).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code is 204 or 200 or 301 or 302 or 404)
                    return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }
}
