using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Fixtures;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Engine-level sibling to <see cref="CrashResumeFuzzTests"/> (TASK-144): instead of a crash, the *source*
/// changes mid-transfer — the primary server dies partway through and a mirror serving the identical content
/// finishes the job. Asserts the same bar as crash-resume: the final file is SHA-256-identical to the
/// reference, and — critically — the mirror only serves the bytes still missing, proving the checkpoint
/// carried over rather than the download restarting from zero. This works purely at the
/// <see cref="ISegmentedDownloader"/>/<see cref="ReceivedRanges"/> level; <see cref="DownloadManager"/>'s
/// own URL-switch (exercised by JustDownload.Tests.Lifecycle.MirrorFailoverTests) relies on exactly this
/// mechanism.
/// </summary>
public sealed class MirrorFailoverResumeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "jd-mirror-resume-" + Guid.NewGuid().ToString("N"));

    public MirrorFailoverResumeTests() => Directory.CreateDirectory(_dir);

    private static byte[] RandomBody(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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

    private sealed class CancelAtProgress : IProgress<long>
    {
        private readonly long _killAt;
        private readonly CancellationTokenSource _cts;

        public CancelAtProgress(long killAt, CancellationTokenSource cts)
        {
            _killAt = killAt;
            _cts = cts;
        }

        public void Report(long value)
        {
            if (value >= _killAt)
            {
                _cts.Cancel();
            }
        }
    }

    [Fact]
    public async Task PrimaryDiesPartway_MirrorFinishes_ResumesToSha256IdenticalFile_WithoutRefetchingHeldBytes()
    {
        byte[] body = RandomBody(300 * 1024);
        string reference = CrashResumeFuzz.Sha256(body);

        await using var primary = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var mirror = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, "out.bin");

        // Attempt 1 against the primary: pause partway so real, correct bytes are already on disk and
        // captured in the checkpoint before the "failure".
        var received = new ReceivedRanges();
        using (var cts = new CancellationTokenSource())
        {
            try
            {
                await downloader.DownloadAsync(
                    new DownloadRequest
                    {
                        Url = primary.Url("file.bin"),
                        DestinationPath = dest,
                        Connections = 4,
                        SpeedLimit = 512 * 1024,
                    },
                    new CancelAtProgress(body.Length / 3, cts),
                    received,
                    cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        long heldBefore = received.TotalReceived;
        heldBefore.Should().BeGreaterThan(0, "the primary must have delivered some real bytes before it 'died'");
        heldBefore.Should().BeLessThan(body.Length, "the primary must not have finished the file");

        // The primary is now unreachable; DownloadManager would fail over here. Simulate that directly:
        // resume the SAME checkpoint against the mirror.
        DownloadResult result = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = mirror.Url("file.bin"),
                DestinationPath = dest,
                Connections = 4,
            },
            received: received);

        result.TotalBytes.Should().Be(body.Length);
        byte[] finalBytes = await File.ReadAllBytesAsync(dest);
        finalBytes.Length.Should().Be(body.Length);
        CrashResumeFuzz.Sha256(finalBytes).Should().Be(
            reference, "the file assembled from primary + mirror bytes must still be byte-identical");

        // The mirror must only have been asked for the still-missing tail, not the whole file again.
        mirror.ServedBytes.Should().BeLessThanOrEqualTo(
            (body.Length - heldBefore) + 1, // +1 tolerance for the 1-byte range probe
            "resuming across a mirror switch must not re-fetch bytes the primary already delivered");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
