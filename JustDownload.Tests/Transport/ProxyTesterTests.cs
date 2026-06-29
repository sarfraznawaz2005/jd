using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using JustDownload.Core.Transport.Proxy;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Proxy connectivity test (TASK-152): a working SOCKS5 proxy reports success and the probe is genuinely
/// tunnelled; an unreachable proxy and a "no proxy" config report a clear failure rather than throwing.
/// </summary>
public sealed class ProxyTesterTests
{
    [Fact]
    public async Task TestAsync_SucceedsThroughAWorkingSocks5Proxy()
    {
        await using var origin = new LoopbackHttpServer { Body = new byte[64], SupportRanges = true };
        await using var socks = new LoopbackSocksProxy();
        var tester = new ProxyTester(origin.Url("probe"), TimeSpan.FromSeconds(10));
        var config = new ProxyConfiguration(ProxyKind.Socks5, "127.0.0.1", socks.Port);

        ProxyTestResult result = await tester.TestAsync(config);

        result.Success.Should().BeTrue(result.Message);
        socks.ConnectedTargets.Should().NotBeEmpty("the probe was tunnelled through the proxy");
    }

    [Fact]
    public async Task TestAsync_FailsWhenTheProxyIsUnreachable()
    {
        var tester = new ProxyTester(new Uri("http://example.invalid/"), TimeSpan.FromSeconds(2));
        var config = new ProxyConfiguration(ProxyKind.Http, "127.0.0.1", ClosedLoopbackPort());

        ProxyTestResult result = await tester.TestAsync(config);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task TestAsync_NoProxy_ReportsNotConfigured()
    {
        var tester = new ProxyTester(new Uri("http://example.invalid/"), TimeSpan.FromSeconds(2));

        ProxyTestResult result = await tester.TestAsync(ProxyConfiguration.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No proxy");
    }

    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
