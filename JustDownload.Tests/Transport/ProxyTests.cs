using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Proxy support (TASK-034, US-6): configuration → handler URI (including the SOCKS5 scheme that resolves
/// DNS remotely, AC1), the global/per-download resolver, the proxy-keyed client pool, and an end-to-end
/// HTTP-proxy route through a loopback proxy with a runtime toggle (AC0/AC2). Full SOCKS server routing is
/// covered by the fixture task; here the SOCKS path is verified at the configuration boundary.
/// </summary>
public sealed class ProxyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-proxy-" + Guid.NewGuid().ToString("N"));

    public ProxyTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(ProxyKind.Http, "http")]
    [InlineData(ProxyKind.Socks4, "socks4")]
    [InlineData(ProxyKind.Socks5, "socks5")]
    public void ToProxyUri_MapsKindToScheme(ProxyKind kind, string expectedScheme)
    {
        var config = new ProxyConfiguration(kind, "127.0.0.1", 1080);

        Uri? uri = config.ToProxyUri();

        uri.Should().NotBeNull();
        uri!.Scheme.Should().Be(expectedScheme);
        uri.Host.Should().Be("127.0.0.1");
        uri.Port.Should().Be(1080);
    }

    [Fact]
    public void Socks5_UsesRemoteDnsScheme()
    {
        // .NET's SocketsHttpHandler resolves DNS at the proxy for the socks5 scheme (AC1).
        new ProxyConfiguration(ProxyKind.Socks5, "proxy", 1080).ToProxyUri()!.Scheme.Should().Be("socks5");
    }

    [Fact]
    public void ToProxyUri_None_IsDirect()
    {
        ProxyConfiguration.None.ToProxyUri().Should().BeNull();
        ProxyConfiguration.None.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ProxyService_GlobalAndOverride_ResolveEffective()
    {
        var service = new ProxyService();
        service.Effective.Should().Be(ProxyConfiguration.None);

        var global = new ProxyConfiguration(ProxyKind.Http, "g", 8080);
        service.SetGlobalProxy(global);
        service.Effective.Should().Be(global, "without an override the global proxy applies");

        var perDownload = new ProxyConfiguration(ProxyKind.Socks5, "d", 1080);
        using (service.BeginDownloadScope(perDownload))
        {
            service.Effective.Should().Be(perDownload, "an override wins for the current flow");
        }

        service.Effective.Should().Be(global, "the override is cleared when its scope ends");
    }

    [Fact]
    public void HttpClientProvider_PoolsByConfiguration()
    {
        var options = new TransportOptions();
        using var shared = new SharedHttpHandlerProvider(options);
        using var provider = new HttpClientProvider(shared, options);

        var http = new ConnectionProfile(new ProxyConfiguration(ProxyKind.Http, "127.0.0.1", 8080));

        provider.GetClient(ConnectionProfile.Direct).Should().BeSameAs(provider.GetClient(ConnectionProfile.Direct));
        provider.GetClient(http).Should().BeSameAs(provider.GetClient(http), "same profile reuses one client");
        provider.GetClient(http).Should().NotBeSameAs(provider.GetClient(ConnectionProfile.Direct));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SegmentationOptions { DefaultConnections = 4, MinSegmentSize = 16 * 1024, MinStealSize = 16 * 1024 });
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    private static byte[] Bytes(int n)
    {
        var d = new byte[n];
        for (int i = 0; i < n; i++)
        {
            d[i] = (byte)((i * 41 + 13) % 256);
        }

        return d;
    }

    [Fact]
    public async Task GlobalHttpProxy_RoutesTraffic_AndCanBeToggledWithoutRestart()
    {
        byte[] body = Bytes(128 * 1024);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var proxy = new LoopbackHttpProxy();
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        var proxyService = provider.GetRequiredService<IProxyService>();

        // Proxy ON: the download must route through the proxy and still be byte-correct.
        proxyService.SetGlobalProxy(new ProxyConfiguration(ProxyKind.Http, "127.0.0.1", proxy.Port));
        string viaProxy = Path.Combine(_dir, "via-proxy.bin");
        await downloader.DownloadAsync(new DownloadRequest { Url = origin.Url("f.bin"), DestinationPath = viaProxy });

        (await File.ReadAllBytesAsync(viaProxy)).Should().Equal(body);
        proxy.RequestedUrls.Should().NotBeEmpty("traffic is routed through the HTTP proxy (AC0)");
        proxy.RequestedUrls.Should().OnlyContain(u => u.Contains("f.bin", StringComparison.Ordinal));

        // Proxy OFF at runtime (no restart): a new download must not hit the proxy.
        int beforeToggle = proxy.RequestedUrls.Count;
        proxyService.SetGlobalProxy(ProxyConfiguration.None);
        string direct = Path.Combine(_dir, "direct.bin");
        await downloader.DownloadAsync(new DownloadRequest { Url = origin.Url("f.bin"), DestinationPath = direct });

        (await File.ReadAllBytesAsync(direct)).Should().Equal(body);
        proxy.RequestedUrls.Count.Should().Be(beforeToggle, "after toggling the proxy off, traffic goes direct (AC2)");
    }

    [Fact]
    public async Task PerDownloadProxy_OverridesGlobalDirect()
    {
        byte[] body = Bytes(64 * 1024);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var proxy = new LoopbackHttpProxy();
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        // Global is direct; this single download overrides to use the proxy.
        string dest = Path.Combine(_dir, "per-download.bin");
        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationPath = dest,
            Proxy = new ProxyConfiguration(ProxyKind.Http, "127.0.0.1", proxy.Port),
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        proxy.RequestedUrls.Should().NotBeEmpty("the per-download proxy override routed this download through the proxy");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
