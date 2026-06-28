using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Fixtures;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Crash-resume fuzz harness (TASK-083, PRD §5.3): kill the transfer at randomized offsets and resume from
/// the checkpoint until done, asserting the final file is SHA-256-identical to the reference (AC0/AC1). This
/// is a normal xUnit test, so it runs in CI (AC2). Resume correctness is the engine's core promise (§5).
/// </summary>
public sealed class CrashResumeFuzzTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "jd-fuzz-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public CrashResumeFuzzTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

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
        // Small floors so a modest body splits into several segments (more interesting kill points).
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

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(99999)]
    public async Task RandomizedKillPoints_ResumeToSha256IdenticalFile(int seed)
    {
        byte[] body = RandomBody(300 * 1024);
        string reference = CrashResumeFuzz.Sha256(body);

        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Path.Combine(_dir, $"fuzz-{seed}.bin");

        // A per-download speed cap keeps the transfer in flight long enough for the kill to land mid-stream.
        CrashResumeFuzz.Result result = await CrashResumeFuzz.RunAsync(
            downloader, server, dest, body.Length, new Random(seed), connections: 4, speedLimit: 512 * 1024);

        _output.WriteLine($"seed {seed}: {result.Kills} kill(s) over {result.Attempts} attempt(s)");
        result.FinalBytes.Length.Should().Be(body.Length);
        CrashResumeFuzz.Sha256(result.FinalBytes).Should().Be(reference, "the resumed file must be byte-identical");
    }

    [Fact]
    public async Task ManyRandomKills_NeverCorruptTheFile()
    {
        byte[] body = RandomBody(256 * 1024);
        string reference = CrashResumeFuzz.Sha256(body);
        var random = new Random(2026);
        int totalKills = 0;

        // Repeat the whole download many times with different random kill points; every result must be exact.
        for (int i = 0; i < 12; i++)
        {
            await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
            using ServiceProvider provider = BuildProvider();
            var downloader = provider.GetRequiredService<ISegmentedDownloader>();
            string dest = Path.Combine(_dir, $"many-{i}.bin");

            CrashResumeFuzz.Result result = await CrashResumeFuzz.RunAsync(
                downloader, server, dest, body.Length, random, connections: 4, speedLimit: 1024 * 1024);

            totalKills += result.Kills;
            CrashResumeFuzz.Sha256(result.FinalBytes).Should().Be(reference, $"iteration {i} must resume exactly");
        }

        totalKills.Should().BeGreaterThan(0, "the harness actually interrupts transfers (not just clean downloads)");
        _output.WriteLine($"total kills across 12 iterations: {totalKills}");
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
