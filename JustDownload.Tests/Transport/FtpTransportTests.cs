using System.Text;
using FluentAssertions;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Ftp;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// FTP transport behaviour (TASK-033) over a fake connection: a ranged request maps to a REST-resumed read
/// reported as 206 so the probe sees range support (AC0), resume reads from the persisted offset (AC1), and
/// the file name comes from the path or, for a directory URL, the listing (AC2). The concrete FluentFTP
/// wrapper is exercised against a real server by the fixture task.
/// </summary>
public sealed class FtpTransportTests
{
    private static FtpTransport Build(FakeFtpFactory factory) =>
        new(factory, NullLogger<FtpTransport>.Instance);

    private static async Task<byte[]> ReadAllAsync(ITransportResponse response)
    {
        await using Stream s = await response.OpenContentStreamAsync();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task SendAsync_PlainGet_Returns200_WithFullBody_AndFileNameFromPath()
    {
        var factory = new FakeFtpFactory { Data = Encoding.ASCII.GetBytes("hello ftp world") };
        await using ITransportResponse response = await Build(factory)
            .SendAsync(new TransportRequest { Uri = new Uri("ftp://host/dir/file.bin") });

        response.StatusCode.Should().Be(200);
        response.IsPartialContent.Should().BeFalse();
        response.ContentLength.Should().Be(factory.Data.Length);
        response.AcceptsRanges.Should().BeTrue();
        response.SuggestedFileName.Should().Be("file.bin");
        (await ReadAllAsync(response)).Should().Equal(factory.Data);
    }

    [Fact]
    public async Task SendAsync_OneByteRangeProbe_Returns206_WithTotalSize()
    {
        // The resource probe sends bytes=0-0; FTP must answer 206 with the total size so segmentation is enabled.
        var factory = new FakeFtpFactory { Data = new byte[5000] };
        await using ITransportResponse response = await Build(factory)
            .SendAsync(new TransportRequest { Uri = new Uri("ftp://host/f.bin"), Range = new ByteRange(0, 0) });

        response.IsPartialContent.Should().BeTrue();
        response.ContentRange.Should().NotBeNull();
        response.ContentRange!.Value.TotalLength.Should().Be(5000);
    }

    [Fact]
    public async Task SendAsync_RangedGet_ResumesViaRest_FromPersistedOffset()
    {
        var factory = new FakeFtpFactory { Data = Encoding.ASCII.GetBytes("0123456789ABCDEF") };
        await using ITransportResponse response = await Build(factory)
            .SendAsync(new TransportRequest { Uri = new Uri("ftp://host/f.bin"), Range = new ByteRange(10, 15) });

        response.IsPartialContent.Should().BeTrue();
        factory.Restarts.Should().Contain(10, "REST restarts from the requested offset (AC1)");
        (await ReadAllAsync(response)).Should().Equal(Encoding.ASCII.GetBytes("ABCDEF"));
    }

    [Fact]
    public async Task SendAsync_DirectoryUrl_DerivesFileNameFromListing()
    {
        var factory = new FakeFtpFactory
        {
            Data = new byte[10],
            Names = ["/pub/movie.mp4"],
        };

        await using ITransportResponse response = await Build(factory)
            .SendAsync(new TransportRequest { Uri = new Uri("ftp://host/pub/") });

        response.SuggestedFileName.Should().Be("movie.mp4", "the name is taken from the listing (AC2)");
    }

    [Fact]
    public async Task SendAsync_DisposingResponse_ClosesConnection()
    {
        var factory = new FakeFtpFactory { Data = new byte[4] };
        FtpTransport transport = Build(factory);

        // Two sequential requests, each disposed before the next: if dispose closes the connection the
        // peak concurrency stays at 1 rather than climbing to 2.
        ITransportResponse first = await transport.SendAsync(new TransportRequest { Uri = new Uri("ftp://host/f.bin") });
        await first.DisposeAsync();
        ITransportResponse second = await transport.SendAsync(new TransportRequest { Uri = new Uri("ftp://host/f.bin") });
        await second.DisposeAsync();

        factory.PeakConcurrency.Should().Be(1, "each connection is closed when its response is disposed");
    }
}
