using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Ftp;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// FTP through the real engine (TASK-033): the scheme-routing transport sends ftp:// to the FTP transport,
/// the segmented downloader splits an FTP file across REST-resumed connections and writes it byte-correct
/// (AC0), and a resume fetches only the remaining gaps via REST from the persisted offsets (AC1).
/// </summary>
public sealed class FtpDownloadIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-ftp-" + Guid.NewGuid().ToString("N"));

    public FtpDownloadIntegrationTests() => Directory.CreateDirectory(_dir);

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 37 + 11) % 256);
        }

        return data;
    }

    private static ServiceProvider BuildProvider(FakeFtpFactory ftp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFtpConnectionFactory>(ftp); // wins over the FluentFTP factory (TryAdd)
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SegmentedFtpDownload_WritesCorrectFile_UsingRestPerSegment()
    {
        byte[] body = Bytes(256 * 1024);
        var ftp = new FakeFtpFactory { Data = body };
        using ServiceProvider provider = BuildProvider(ftp);
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "ftp.bin");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = new Uri("ftp://host/pub/file.bin"),
            DestinationPath = dest,
            Connections = 4,
        });

        result.SingleConnection.Should().BeFalse("FTP supports REST, so the file is segmented");
        result.InitialSegments.Should().Be(4);
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        ftp.Restarts.Should().Contain(r => r > 0, "segments after the first resume via REST from their offset");
        ftp.PeakConcurrency.Should().BeGreaterThan(1, "connections download in parallel");
    }

    [Fact]
    public async Task ResumeFtpDownload_FetchesOnlyGaps_ViaRest()
    {
        byte[] body = Bytes(200 * 1024);
        var ftp = new FakeFtpFactory { Data = body };
        using ServiceProvider provider = BuildProvider(ftp);
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "resume.bin");

        // Pre-seed the first half as already received; only the second half should be fetched.
        var received = new ReceivedRanges();
        received.Add(0, body.Length / 2);

        DownloadResult result = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = new Uri("ftp://host/file.bin"),
                DestinationPath = dest,
                Connections = 2,
            },
            received: received);

        result.TotalBytes.Should().Be(body.Length);
        (await File.ReadAllBytesAsync(dest))[(body.Length / 2)..].Should().Equal(body[(body.Length / 2)..]);

        // The probe issues a bytes=0-0 read (restart 0); every actual data read starts within the missing
        // second half — none re-fetches the already-received first half.
        ftp.Restarts.Should().NotContain(r => r > 0 && r < body.Length / 2,
            "resume re-opens reads only from offsets in the missing second half (REST)");
        ftp.Restarts.Should().Contain(r => r >= body.Length / 2);
    }

    [Fact]
    public async Task SchemeRoutingTransport_UnsupportedScheme_Throws()
    {
        var options = new TransportOptions();
        var ftp = new FtpTransport(new FakeFtpFactory(), NullLogger<FtpTransport>.Instance);
        var http = new HttpTransport(new SharedHttpHandlerProvider(options), options);
        var router = new SchemeRoutingTransport(http, ftp);

        Func<Task> act = () => router.SendAsync(new TransportRequest { Uri = new Uri("gopher://host/x") });

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
