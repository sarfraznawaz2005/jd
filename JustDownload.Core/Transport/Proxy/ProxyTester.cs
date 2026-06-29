using System.Net;
using System.Net.Http;
using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Transport.Proxy;

/// <summary>The outcome of a proxy connectivity test (TASK-152): whether it succeeded and a human-readable message.</summary>
public sealed record ProxyTestResult(bool Success, string Message);

/// <summary>
/// Verifies a proxy configuration before it is saved (TASK-152) by making one small request through it and
/// reporting whether the proxy is reachable, refuses auth (407), or fails to connect. User-initiated and
/// one-shot — the only outbound traffic is this single probe (CLAUDE.md §5 / D2).
/// </summary>
public interface IProxyTester
{
    Task<ProxyTestResult> TestAsync(ProxyConfiguration proxy, CancellationToken cancellationToken = default);
}

internal sealed class ProxyTester : IProxyTester
{
    // example.com is the IANA-reserved documentation domain — a neutral, non-tracking endpoint to probe.
    private static readonly Uri DefaultProbeUrl = new("http://example.com/");

    private readonly Uri _probeUrl;
    private readonly TimeSpan _timeout;

    public ProxyTester()
        : this(DefaultProbeUrl, TimeSpan.FromSeconds(10))
    {
    }

    internal ProxyTester(Uri probeUrl, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(probeUrl);
        _probeUrl = probeUrl;
        _timeout = timeout;
    }

    public async Task<ProxyTestResult> TestAsync(ProxyConfiguration proxy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        if (!proxy.IsEnabled)
        {
            return new ProxyTestResult(false, "No proxy is configured.");
        }

        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(proxy.ToProxyUri()) { Credentials = ToNetworkCredential(proxy.Credentials) },
            ConnectTimeout = _timeout,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { Timeout = _timeout };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, _probeUrl);
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                return new ProxyTestResult(false, "The proxy requires authentication (407) — check the username and password.");
            }

            return new ProxyTestResult(true, $"Connected through the proxy ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProxyTestResult(false, "The proxy did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return new ProxyTestResult(false, $"Could not connect through the proxy: {ex.Message}");
        }
    }

    private static NetworkCredential? ToNetworkCredential(NetworkCredentials? credentials) =>
        credentials is null
            ? null
            : new NetworkCredential(credentials.Username, credentials.Password, credentials.Domain ?? string.Empty);
}
