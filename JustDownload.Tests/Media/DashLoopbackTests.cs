using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The DASH SegmentTemplate/SegmentList path end-to-end (TASK-102, AC0 "a SegmentTemplate-based MPD downloads
/// and muxes correctly"): a real ffmpeg-generated DASH manifest (SegmentTemplate + SegmentTimeline, exactly
/// the shape real-world DASH content uses) is served over a live loopback HTTP server; the real extractor
/// recognises it as <see cref="MediaKind.Dash"/>, the real <see cref="IMediaDownloadCoordinator"/> downloads
/// both representations' segments, concatenates them, and muxes them by stream copy — and the output is a
/// file ffprobe reads as valid with both the original h264 video and aac audio streams intact. Skips (rather
/// than fails) when ffmpeg is not installed on the host, matching <c>MediaMuxerTests</c>' precedent. Every
/// ffmpeg/ffprobe process spawned is awaited to completion (CLAUDE.md §2.5) — none are left running.
/// </summary>
public sealed class DashLoopbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-dash-e2e-" + Guid.NewGuid().ToString("N"));

    public DashLoopbackTests() => Directory.CreateDirectory(_dir);

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();

        // The yt-dlp fallback extractor (TASK-163) needs ISettingsService; substitute a no-DB fake with the
        // (default, off) video-capture toggle rather than pulling in the full SQLite-backed settings store.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);

        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SegmentTemplateMpd_DownloadsAndMuxes_ToPlayableOutput_WithBothStreamsIntact()
    {
        using ServiceProvider provider = BuildProvider();
        var locator = provider.GetRequiredService<IFfmpegLocator>();
        FfmpegInfo? ffmpegInfo = await locator.LocateAsync();
        if (ffmpegInfo is null)
        {
            return; // ffmpeg not installed on this host — skip, matching MediaMuxerTests' precedent.
        }

        string videoFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ts");
        if (!File.Exists(videoFixture))
        {
            return;
        }

        string mpdPath = await GenerateDashFixtureAsync(ffmpegInfo, videoFixture, _dir);

        await using var server = new LoopbackDashServer(_dir);

        // The real extractor recognises the manifest and reports both representations.
        MediaSource? source = await provider.GetRequiredService<IMediaExtractorRegistry>()
            .ExtractAsync(new MediaRequest { Url = server.Url(Path.GetFileName(mpdPath)) });

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Dash);
        source.Variants.Should().ContainSingle();
        source.AudioVariants.Should().ContainSingle();

        // The real coordinator: segment download -> concat -> mux, exactly the production download path.
        string outputPath = Path.Combine(_dir, "output.mkv");
        string workingDirectory = Path.Combine(_dir, "work");
        var coordinator = provider.GetRequiredService<IMediaDownloadCoordinator>();

        MediaDownloadOutcome outcome = await coordinator.DownloadAsync(new MediaDownloadRequest
        {
            Kind = MediaKind.Dash,
            MediaUrl = new Uri(source.Variants[0].Id),
            AudioUrl = new Uri(source.AudioVariants[0].Id),
            Container = MediaContainer.Mkv,
            OutputPath = outputPath,
            WorkingDirectory = workingDirectory,
        });

        outcome.TotalBytes.Should().BeGreaterThan(0);
        File.Exists(outputPath).Should().BeTrue();

        // ffprobe must read it as a valid container with the original h264 video and aac audio, stream-copied.
        string streams = await ProbeCodecsAsync(outputPath);
        streams.Should().Contain("h264").And.Contain("video", "video is stream-copied unchanged");
        streams.Should().Contain("aac").And.Contain("audio", "audio is stream-copied unchanged");
    }

    /// <summary>
    /// Generates a real DASH SegmentTemplate/SegmentTimeline fixture with ffmpeg itself: a short H.264 video
    /// (re-encoded from the repo's MPEG-TS fixture) plus a synthesised AAC tone as a separate audio
    /// representation, muxed into two <c>AdaptationSet</c>s so the manifest matches real-world separate
    /// video+audio DASH content exactly (verified against ffmpeg 7.1's actual output shape). Runs ffmpeg
    /// directly (not through <see cref="IFfmpegRunner"/>) with an explicit working directory — ffmpeg's DASH
    /// muxer resolves its init/media segment filenames against the process's current directory, not the
    /// manifest's, so the working directory must be set for the segments to land next to the manifest.
    /// </summary>
    private static async Task<string> GenerateDashFixtureAsync(FfmpegInfo ffmpeg, string videoFixture, string outputDir)
    {
        string audioPath = Path.Combine(outputDir, "tone.m4a");
        FfmpegRunResult tone = await RunFfmpegAsync(
            ffmpeg, outputDir, ["-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "aac", audioPath]);
        tone.Succeeded.Should().BeTrue($"audio fixture generation should succeed; stderr: {tone.StandardError}");

        const string mpdFileName = "manifest.mpd";
        FfmpegRunResult dash = await RunFfmpegAsync(
            ffmpeg,
            outputDir,
            [
                "-y", "-i", videoFixture, "-i", audioPath,
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "copy",
                "-map", "0:v:0", "-map", "1:a:0",
                "-f", "dash", "-use_timeline", "1", "-use_template", "1", "-seg_duration", "1",
                "-adaptation_sets", "id=0,streams=v id=1,streams=a",
                mpdFileName,
            ]);
        dash.Succeeded.Should().BeTrue($"DASH fixture generation should succeed; stderr: {dash.StandardError}");

        return Path.Combine(outputDir, mpdFileName);
    }

    private static async Task<FfmpegRunResult> RunFfmpegAsync(FfmpegInfo ffmpeg, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(ffmpeg.ExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)!;
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new FfmpegRunResult(process.ExitCode, await stdErrTask.ConfigureAwait(false));
    }

    private static async Task<string> ProbeCodecsAsync(string path)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type,codec_name");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
