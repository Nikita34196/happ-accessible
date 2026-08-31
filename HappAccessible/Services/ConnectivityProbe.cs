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

    public static async Task<bool> ProbeMixedPortAsync(int mixedPort, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, mixedPort, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
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
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public static async Task<bool> WaitForMixedPortReadyAsync(
        int mixedPort,
        Process process,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                return false;
            if (await ProbeMixedPortAsync(mixedPort, ct).ConfigureAwait(false))
                return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return await ProbeMixedPortAsync(mixedPort, ct).ConfigureAwait(false);
    }

    public static async Task<bool> ProbeHttpViaProxyAsync(
        int mixedPort,
        CancellationToken ct = default,
        int attempts = 1,
        TimeSpan? retryDelay = null) =>
        await ProbeHttpLatencyViaProxyAsync(mixedPort, ct, attempts, retryDelay).ConfigureAwait(false) is not null;

    /// <summary>HTTP round-trip via local mixed port — reflects real tunnel latency.</summary>
    public static async Task<int?> ProbeHttpLatencyViaProxyAsync(
        int mixedPort,
        CancellationToken ct = default,
        int attempts = 1,
        TimeSpan? retryDelay = null)
    {
        retryDelay ??= TimeSpan.FromSeconds(2);
        var urls = new[]
        {
            "http://www.gstatic.com/generate_204",
            "http://connectivitycheck.gstatic.com/generate_204",
            "http://cp.cloudflare.com/"
        };

        int? best = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            using var handler = CreateProxyHandler(mixedPort);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(TimeSpan.FromSeconds(8));
                    using var resp = await client.GetAsync(url, linked.Token).ConfigureAwait(false);
                    sw.Stop();
                    var code = (int)resp.StatusCode;
                    if (code is 204 or 200 or 301 or 302 or 404)
                    {
                        var ms = (int)sw.ElapsedMilliseconds;
                        best = best is null ? ms : Math.Min(best.Value, ms);
                    }
                }
                catch
                {
                    // try next
                }
            }

            if (best is not null)
                return best;

            if (attempt + 1 < attempts)
                await Task.Delay(retryDelay.Value, ct).ConfigureAwait(false);
        }

        return best;
    }

    /// <summary>Full session probe: mixed port, HTTP, HTTPS site, DNS.</summary>
    public static async Task<(bool Ok, string Detail)> ProbeSessionHealthAsync(
        int mixedPort, CancellationToken ct = default)
    {
        if (!await ProbeMixedPortAsync(mixedPort, ct).ConfigureAwait(false))
            return (false, "локальный mixed-порт не отвечает");

        if (await ProbeHttpLatencyViaProxyAsync(mixedPort, ct).ConfigureAwait(false) is null)
            return (false, "HTTP через туннель не отходит");

        if (!await ProbeHttpsSiteViaProxyAsync(mixedPort, "https://example.com/", ct).ConfigureAwait(false))
            return (false, "HTTPS через туннель не отходит");

        // HTTP/HTTPS already prove routing works; DNS-over-HTTPS probe is best-effort only
        // (Cloudflare DoH often fails via HTTP proxy without Accept header — caused false reconnects).
        if (!await ProbeDnsViaProxyAsync(mixedPort, ct).ConfigureAwait(false))
            return (true, "ok (DNS probe inconclusive)");

        return (true, "ok");
    }

    private static async Task<bool> ProbeHttpsSiteViaProxyAsync(
        int mixedPort, string url, CancellationToken ct)
    {
        try
        {
            using var handler = CreateProxyHandler(mixedPort);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await client.GetAsync(url, linked.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode || (int)resp.StatusCode is 301 or 302;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeDnsViaProxyAsync(int mixedPort, CancellationToken ct)
    {
        var urls = new[]
        {
            "https://cloudflare-dns.com/dns-query?name=example.com&type=A",
            "http://cloudflare-dns.com/dns-query?name=example.com&type=A"
        };

        foreach (var url in urls)
        {
            try
            {
                using var handler = CreateProxyHandler(mixedPort);
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(6));
                using var resp = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static HttpClientHandler CreateProxyHandler(int mixedPort) =>
        new()
        {
            Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
}
