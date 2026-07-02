using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Fakes;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Multi-mirror / multi-source failover (TASK-144, the single AC: "A download with mirrors fails over to an
/// alternate on error and completes correctly"). Two or three real loopback HTTP servers stand in for the
/// primary and its mirrors: the primary is made to fail (a forced error status, simulating it being down),
/// and <see cref="DownloadManager"/> must walk <see cref="Download.AlternateUrls"/> in order, verifying each
/// candidate's probed size against the one already known for this download before trusting it (the cheapest
/// available guard against splicing two different resources together mid-resume), and complete the download
/// with a byte-correct file once a good mirror is found.
/// </summary>
public sealed class MirrorFailoverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public MirrorFailoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        // Zero-delay retry budget so a genuinely transient classification never slows the test down.
        services.AddSingleton<IRetryBackoff>(new FakeRetryBackoff(maxRetries: 2));
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private IDownloadManager Manager => _provider.GetRequiredService<IDownloadManager>();

    private IDownloadRepository Downloads => _provider.GetRequiredService<IDownloadRepository>();

    private static byte[] RandomBody(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task PrimaryDown_FailsOverToWorkingMirror_CompletesWithCorrectHash_AndPersistsSwitchedUrl()
    {
        byte[] body = RandomBody(64 * 1024);
        string reference = Sha256(body);

        await using var primary = new LoopbackHttpServer { StatusOverride = 500 };
        await using var mirror = new LoopbackHttpServer { Body = body, SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = primary.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 4,
            AlternateUrls = [mirror.Url("file.bin")],
        });

        DownloadResult result = await Manager.StartAsync(id);

        result.TotalBytes.Should().Be(body.Length);
        byte[] finalBytes = await File.ReadAllBytesAsync(Path.Combine(_tempDir, "out.bin"));
        Sha256(finalBytes).Should().Be(reference, "the file must come through byte-correct after failover");

        Download? saved = await Downloads.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Completed);
        saved.Url.Should().Be(mirror.Url("file.bin").ToString(), "the active source is switched to the mirror that worked");
    }

    [Fact]
    public async Task PrimaryAndFirstMirrorBothDown_SecondMirrorWorks_Completes()
    {
        byte[] body = RandomBody(64 * 1024);
        string reference = Sha256(body);

        await using var primary = new LoopbackHttpServer { StatusOverride = 500 };
        await using var deadMirror = new LoopbackHttpServer { StatusOverride = 503 };
        await using var goodMirror = new LoopbackHttpServer { Body = body, SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = primary.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 4,
            AlternateUrls = [deadMirror.Url("file.bin"), goodMirror.Url("file.bin")],
        });

        DownloadResult result = await Manager.StartAsync(id);

        result.TotalBytes.Should().Be(body.Length);
        byte[] finalBytes = await File.ReadAllBytesAsync(Path.Combine(_tempDir, "out.bin"));
        Sha256(finalBytes).Should().Be(reference);

        (await Downloads.GetAsync(id))!.Url.Should().Be(goodMirror.Url("file.bin").ToString());
    }

    [Fact]
    public async Task MismatchedSizeMirror_IsSkipped_NotTrustedAsIdentical()
    {
        byte[] body = RandomBody(64 * 1024);

        await using var primary = new LoopbackHttpServer { StatusOverride = 500 };
        // A same-name mirror that happens to serve a DIFFERENT-length body — must never be spliced in.
        await using var wrongSizeMirror = new LoopbackHttpServer
        {
            Body = RandomBody(64 * 1024 + 4096),
            SupportRanges = true,
        };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = primary.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 4,
            TotalBytes = body.Length, // the size this download is known to be
            AlternateUrls = [wrongSizeMirror.Url("file.bin")],
        });

        Func<Task> act = async () => await Manager.StartAsync(id);
        await act.Should().ThrowAsync<Exception>();

        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Failed);

        // The mismatched mirror's full body must never have been fetched — only the tiny probe range, if that.
        wrongSizeMirror.ServedBytes.Should().BeLessThanOrEqualTo(
            1, "a size-mismatched mirror must be rejected after the probe, before any real content is fetched");
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
