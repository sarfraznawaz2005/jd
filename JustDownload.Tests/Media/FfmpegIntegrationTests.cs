using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// ffmpeg integration (TASK-040): version parsing (pure, AC0), and — when a real ffmpeg is present —
/// locating it (AC0), reporting progress from a short transcode (AC1), and terminating cleanly on
/// cancellation without leaving an orphan (AC2). On a machine without ffmpeg the integration cases
/// assert the not-found contract instead, so the suite is portable.
/// </summary>
public sealed class FfmpegIntegrationTests
{
    [Theory]
    [InlineData("ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright (c)", "7.1.1-full_build-www.gyan.dev")]
    [InlineData("ffmpeg version n7.0 Copyright", "n7.0")]
    [InlineData("ffmpeg version 6.1", "6.1")]
    [InlineData("not ffmpeg at all", null)]
    [InlineData("", null)]
    public void ParseVersion_ExtractsTheVersionToken(string line, string? expected)
    {
        FfmpegLocator.ParseVersion(line).Should().Be(expected);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Locate_FindsFfmpeg_OrReportsNotFound()
    {
        using ServiceProvider provider = BuildProvider();
        var locator = provider.GetRequiredService<IFfmpegLocator>();

        FfmpegInfo? info = await locator.LocateAsync();

        if (info is null)
        {
            // No ffmpeg available — the documented contract is a null result. Nothing else to assert.
            return;
        }

        info.Version.Should().NotBeNullOrWhiteSpace();
        info.Version.Should().MatchRegex(@"\d", "a version string contains a digit");
        info.ExecutablePath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Run_TranscodesShortClip_ReportsProgress_AndExitsCleanly()
    {
        using ServiceProvider provider = BuildProvider();
        if (await provider.GetRequiredService<IFfmpegLocator>().LocateAsync() is null)
        {
            return; // ffmpeg not installed on this host.
        }

        var runner = provider.GetRequiredService<IFfmpegRunner>();
        var snapshots = new List<FfmpegProgress>();
        var progress = new Progress<FfmpegProgress>(p =>
        {
            lock (snapshots)
            {
                snapshots.Add(p);
            }
        });

        // Generate 0.3s of a tone and discard the output — no file, just exercise progress + exit.
        FfmpegRunResult result = await runner.RunAsync(
            ["-f", "lavfi", "-i", "sine=frequency=440:duration=0.3", "-f", "null", "-"],
            progress);

        result.Succeeded.Should().BeTrue($"ffmpeg should succeed; stderr: {result.StandardError}");
        await Task.Delay(50); // let the final progress callback drain
        lock (snapshots)
        {
            snapshots.Should().NotBeEmpty("ffmpeg should report at least one progress block");
            snapshots.Should().Contain(p => p.IsComplete, "a terminating progress=end block is expected");
        }
    }

    [Fact]
    public async Task Run_Cancellation_KillsProcessPromptly()
    {
        using ServiceProvider provider = BuildProvider();
        if (await provider.GetRequiredService<IFfmpegLocator>().LocateAsync() is null)
        {
            return;
        }

        var runner = provider.GetRequiredService<IFfmpegRunner>();
        using var cts = new CancellationTokenSource();

        // -re reads input in real time, so this would otherwise run for 30s; we cancel after 200ms and
        // expect prompt termination (well under the 30s) rather than a wait for completion.
        var stopwatch = Stopwatch.StartNew();
        Func<Task> run = async () =>
        {
            Task<FfmpegRunResult> task = runner.RunAsync(
                ["-re", "-f", "lavfi", "-i", "sine=frequency=440:duration=30", "-f", "null", "-"],
                progress: null,
                cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            await task;
        };

        await run.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the process should be killed, not awaited");
    }
}
