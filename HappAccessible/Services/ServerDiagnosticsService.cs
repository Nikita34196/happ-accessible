using HappAccessible.Models;

namespace HappAccessible.Services;

public sealed class ServerDiagnosticsReport
{
    public required ServerProfile Server { get; init; }
    public bool CoreRunning { get; init; }
    public bool TcpOk { get; init; }
    public int? TcpMs { get; init; }
    public bool TunnelOk { get; init; }
    public int? TunnelMs { get; init; }
    public bool DnsOk { get; init; }
    public int? DnsMs { get; init; }
    public bool DirectInternetOk { get; init; }

    public string BuildSummary(bool connectedToServer)
    {
        var parts = new List<string>();
        if (connectedToServer)
            parts.Add(CoreRunning ? "ядро: работает" : "ядро: не запущено");
        parts.Add(TcpOk ? $"TCP: {TcpMs} мс" : "TCP: нет ответа");
        if (connectedToServer)
            parts.Add(TunnelOk ? $"туннель: {TunnelMs} мс" : "туннель: нет ответа");
        if (connectedToServer)
            parts.Add(DnsOk ? $"DNS через туннель: {DnsMs} мс" : "DNS через туннель: сбой");
        return string.Join("; ", parts) + ".";
    }
}

public static class ServerDiagnosticsService
{
    public static async Task<ServerDiagnosticsReport> RunAsync(
        ServerProfile server,
        bool connectedToServer,
        bool coreRunning,
        int? mixedPort,
        CancellationToken ct = default)
    {
        int? tcpMs = null;
        var tcpOk = false;
        if (!string.IsNullOrWhiteSpace(server.Host) && server.Port > 0)
        {
            tcpMs = await PingService.PingAsync(server, ct).ConfigureAwait(false);
            tcpOk = tcpMs is not null;
        }

        int? tunnelMs = null;
        var tunnelOk = false;
        int? dnsMs = null;
        var dnsOk = false;
        if (connectedToServer && mixedPort is int port)
        {
            tunnelMs = await PingService.PingViaTunnelAsync(port, ct).ConfigureAwait(false);
            tunnelOk = tunnelMs is not null;
            (dnsOk, dnsMs) = await ProbeDnsViaProxyAsync(port, ct).ConfigureAwait(false);
        }

        return new ServerDiagnosticsReport
        {
            Server = server,
            CoreRunning = coreRunning,
            TcpOk = tcpOk,
            TcpMs = tcpMs,
            TunnelOk = tunnelOk,
            TunnelMs = tunnelMs,
            DnsOk = dnsOk,
            DnsMs = dnsMs,
            DirectInternetOk = false
        };
    }

    private static async Task<(bool Ok, int? Ms)> ProbeDnsViaProxyAsync(int mixedPort, CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var handler = new System.Net.Http.HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{mixedPort}"),
                UseProxy = true
            };
            using var client = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await client.GetAsync("http://cloudflare-dns.com/dns-query?name=example.com&type=A",
                linked.Token).ConfigureAwait(false);
            sw.Stop();
            return (resp.IsSuccessStatusCode, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return (false, null);
        }
    }
}
