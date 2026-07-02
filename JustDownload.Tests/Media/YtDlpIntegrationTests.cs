using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// yt-dlp integration (TASK-162, D3): the DI-wired <see cref="IYtDlpLocator"/> resolves and, when a real
/// yt-dlp is present on the machine, self-validates by successfully reporting a version. On a machine
/// without yt-dlp the not-found contract is asserted instead, so the suite is portable — matching
/// <c>FfmpegIntegrationTests</c>'s pattern for the same reason.
/// </summary>
public sealed class YtDlpIntegrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Locate_FindsYtDlp_OrReportsNotFound()
    {
        using ServiceProvider provider = BuildProvider();
        var locator = provider.GetRequiredService<IYtDlpLocator>();

        YtDlpInfo? info = await locator.LocateAsync();

        if (info is null)
        {
            // No yt-dlp available on this machine — the documented contract is a null result.
            return;
        }

        info.Version.Should().NotBeNullOrWhiteSpace();
        info.ExecutablePath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ServiceCollection_RegistersLocatorAndManifest()
    {
        // Mirrors FfmpegIntegrationTests's scope: AddJustDownloadMedia alone (without the rest of
        // AddJustDownloadCore) is enough to resolve the locator and the pinned manifest. The provisioner's
        // extra dependencies (checksum verifier, shared HTTP handler, app info) come from AddJustDownloadCore
        // and are exercised end-to-end by YtDlpProvisionerTests instead.
        using ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<IYtDlpLocator>().Should().NotBeNull();
        provider.GetRequiredService<YtDlpManifest>().Should().BeSameAs(YtDlpManifest.Default);
    }
}
