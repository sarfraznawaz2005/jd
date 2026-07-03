using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// TASK-186: <see cref="FfmpegOptions.VendorDirectory"/> and <see cref="YtDlpOptions.VendorDirectory"/> used
/// to stay <see langword="null"/> until a provisioner set them as a side effect of a successful download —
/// an in-memory-only mutation. A fresh app restart got a brand-new options instance back at null, so
/// <c>YtDlpLocator</c>/<c>FfmpegLocator</c> skipped the vendor-directory candidate entirely and reported a
/// previously-downloaded tool as "not installed" (user-reported: yt-dlp showed "Ready" right after
/// downloading it, then "not installed" after restarting the app). Both options types must now resolve a
/// real default from DI registration, independent of whether a provisioner has ever run in this process.
/// </summary>
public sealed class VendorDirectoryDefaultsTests
{
    [Fact]
    public void FfmpegOptions_VendorDirectory_HasARealDefault_NotNull()
    {
        using ServiceProvider provider = new ServiceCollection().AddJustDownloadCore().BuildServiceProvider();

        var options = provider.GetRequiredService<FfmpegOptions>();

        options.VendorDirectory.Should().NotBeNullOrWhiteSpace();
        options.VendorDirectory.Should().EndWith("ffmpeg");
    }

    [Fact]
    public void YtDlpOptions_VendorDirectory_HasARealDefault_NotNull()
    {
        using ServiceProvider provider = new ServiceCollection().AddJustDownloadCore().BuildServiceProvider();

        var options = provider.GetRequiredService<YtDlpOptions>();

        options.VendorDirectory.Should().NotBeNullOrWhiteSpace();
        options.VendorDirectory.Should().EndWith("yt-dlp");
    }

    [Fact]
    public void VendorDirectory_IsStableAcrossFreshServiceProviders_SimulatingARestart()
    {
        // The exact bug: a fresh ServiceProvider (a fresh process, in production) must resolve to the same
        // vendor directory a provisioner would have written a tool into during an earlier run -- not null.
        using ServiceProvider first = new ServiceCollection().AddJustDownloadCore().BuildServiceProvider();
        using ServiceProvider second = new ServiceCollection().AddJustDownloadCore().BuildServiceProvider();

        first.GetRequiredService<YtDlpOptions>().VendorDirectory
            .Should().Be(second.GetRequiredService<YtDlpOptions>().VendorDirectory);
    }
}
