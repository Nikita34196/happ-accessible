using System.IO;
using System.Net;
using System.Net.Http;

namespace HappAccessible.Services;

/// <summary>HTTP client that bypasses Windows system proxy (for GitHub updates/downloads).</summary>
public static class DirectHttp
{
    public static readonly string[] GitHubDomainSuffixes =
    [
        "github.com",
        "githubusercontent.com",
        "github.io"
    ];

    public static HttpClient Create(TimeSpan? timeout = null, string? userAgent = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseProxy = false,
            Proxy = null
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
        if (!string.IsNullOrWhiteSpace(userAgent))
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    public static bool IsSslOrTransportFailure(Exception ex)
    {
        while (true)
        {
            if (ex is HttpRequestException or IOException)
            {
                var msg = ex.Message;
                if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("transport", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (ex.InnerException is { } inner)
            {
                ex = inner;
                continue;
            }

            return false;
        }
    }
}
