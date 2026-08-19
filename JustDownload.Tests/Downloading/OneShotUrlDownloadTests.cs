using System.Text;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Transport;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// A download must cost the server exactly one body request when it cannot be segmented (TASK-262). The
/// old flow probed and then fetched again, which wasted a round trip on every range-less download and
/// broke one-shot URLs outright — a single-use token, a signed link or a router CGI answers once, so the
/// second request came back as an error page and that error page was what landed on disk.
/// </summary>
public sealed class OneShotUrlDownloadTests : IDisposable
{
    private readonly string _tempDir;

    public OneShotUrlDownloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-oneshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string Dest(string name) => Path.Combine(_tempDir, name);

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 17 + 3) % 256);
        }

        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OneShotUrl_DownloadsTheRealFile_NotTheRejectionPage()
    {
        // The regression this fixes: the second request's error JSON used to be saved as the "download".
        byte[] body = Bytes(64 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = false,
            SingleUse = true,
        };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("backup.zip");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("api/settings/backup"),
            DestinationPath = dest,
            Connections = 16,
        });

        result.SingleConnection.Should().BeTrue();
        result.TotalBytes.Should().Be(body.Length);
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        server.BodyRequests.Should().Be(1, "a one-shot URL only ever answers the first request");
    }

    [Fact]
    public async Task RangeLessDownload_CostsExactlyOneRequest()
    {
        // Same saving on any ordinary range-less server: the probe response *is* the download.
        byte[] body = Bytes(32 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("plain.bin");

        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 8,
        });

        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        server.BodyRequests.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_KeepsTheBody_WhenTheServerIgnoresTheRange()
    {
        // The probe seam itself: a 200 to a ranged GET means the whole resource is already arriving.
        byte[] body = Bytes(4096);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        await using ProbedResource probed = await probe.OpenAsync(server.Url("file.bin"));

        probed.Result.SupportsRanges.Should().BeFalse();
        probed.Body.Should().NotBeNull();

        await using Stream content = await probed.Body!.OpenContentStreamAsync();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(body);
        server.BodyRequests.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_KeepsNoBody_WhenTheServerHonoursTheRange()
    {
        // A 206 body is one byte, not the resource — the segments fetch their own ranges instead.
        await using var server = new LoopbackHttpServer { Body = Bytes(4096), SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        await using ProbedResource probed = await probe.OpenAsync(server.Url("file.bin"));

        probed.Result.SupportsRanges.Should().BeTrue();
        probed.Body.Should().BeNull();
    }

    [Fact]
    public async Task SegmentedDownload_StillWorks_AndReleasesTheProbeConnection()
    {
        // The reuse must not pin a connection on the path that does not use it (AC: segmented unaffected).
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("segmented.bin");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 4,
        });

        result.SingleConnection.Should().BeFalse();
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
    }

    [Fact]
    public async Task ProbeAsync_StillDiscardsTheBody_ForMetadataOnlyCallers()
    {
        // ProbeAsync is now OpenAsync + dispose; it must not leak the held response to its callers.
        await using var server = new LoopbackHttpServer
        {
            Body = Encoding.UTF8.GetBytes("hello world"),
            SupportRanges = false,
            ContentDisposition = "attachment; filename=\"report.csv\"",
        };
        using ServiceProvider provider = BuildProvider();
        var probe = provider.GetRequiredService<IResourceProbe>();

        ResourceProbeResult result = await probe.ProbeAsync(server.Url("download"));

        result.SuggestedFileName.Should().Be("report.csv");
        result.TotalLength.Should().Be(11);
        result.SupportsRanges.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a failed test must not mask that test's own failure.
        }
    }
}
