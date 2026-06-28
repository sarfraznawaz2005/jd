using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// A/V muxing (TASK-041): the ffmpeg command is a pure stream copy with explicit maps and the right
/// container (verified with a fake runner, AC1/AC2), and — when real ffmpeg is present — muxing a video and
/// a generated audio track yields a single file ffprobe reads as a valid container with both streams
/// stream-copied (AC0). Spawned ffmpeg/ffprobe are awaited/disposed so none are left orphaned (§2.5).
/// </summary>
public sealed class MediaMuxerTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string Temp(string suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jd-mux-{Guid.NewGuid():N}-{suffix}");
        _temp.Add(path);
        return path;
    }

    // --- Fake-runner tests (no ffmpeg needed) ----------------------------------------------------

    private sealed class CapturingRunner : IFfmpegRunner
    {
        public IReadOnlyList<string> LastArguments { get; private set; } = [];

        public Task<FfmpegRunResult> RunAsync(
            IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            // The output path is the final argument — create it so the muxer treats the run as successful.
            File.WriteAllText(arguments[^1], "muxed");
            return Task.FromResult(new FfmpegRunResult(0, string.Empty));
        }
    }

    private MuxRequest BuildRequest(MediaContainer preferred, string? videoCodec, string? audioCodec)
    {
        string video = Temp("v.bin");
        string audio = Temp("a.bin");
        File.WriteAllText(video, "v");
        File.WriteAllText(audio, "a");
        return new MuxRequest
        {
            VideoPath = video,
            AudioPath = audio,
            PreferredContainer = preferred,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            // OutputPath left null so the chosen container drives the extension.
        };
    }

    [Fact]
    public async Task MuxAsync_UsesStreamCopy_WithExplicitMaps()
    {
        var runner = new CapturingRunner();
        var muxer = new MediaMuxer(runner, NullLogger<MediaMuxer>.Instance);
        MuxRequest request = BuildRequest(MediaContainer.Mp4, "h264", "aac") with { OutputPath = Temp("o.mp4") };

        await muxer.MuxAsync(request);

        runner.LastArguments.Should().ContainInConsecutiveOrder("-c", "copy");
        runner.LastArguments.Should().Contain("0:v?").And.Contain("1:a?");
        runner.LastArguments.Should().NotContain(a =>
            a.StartsWith("libx", StringComparison.Ordinal) || a == "-crf" || a == "-b:v",
            "a stream copy must not re-encode");
    }

    [Fact]
    public async Task MuxAsync_Mp4CompatibleCodecs_ProducesMp4_WithFaststart()
    {
        var runner = new CapturingRunner();
        var muxer = new MediaMuxer(runner, NullLogger<MediaMuxer>.Instance);

        MuxResult result = await muxer.MuxAsync(BuildRequest(MediaContainer.Mp4, "h264", "aac"));

        result.Container.Should().Be(MediaContainer.Mp4);
        result.OutputPath.Should().EndWith(".mp4");
        runner.LastArguments.Should().ContainInConsecutiveOrder("-movflags", "+faststart");
    }

    [Fact]
    public async Task MuxAsync_IncompatibleCodecs_FallsBackToMkv()
    {
        var runner = new CapturingRunner();
        var muxer = new MediaMuxer(runner, NullLogger<MediaMuxer>.Instance);

        MuxResult result = await muxer.MuxAsync(BuildRequest(MediaContainer.Mp4, "vp9", "opus"));

        result.Container.Should().Be(MediaContainer.Mkv);
        result.OutputPath.Should().EndWith(".mkv");
    }

    [Fact]
    public async Task MuxAsync_MissingInput_Throws()
    {
        var muxer = new MediaMuxer(new CapturingRunner(), NullLogger<MediaMuxer>.Instance);
        var request = new MuxRequest
        {
            VideoPath = Temp("ghost-v"),
            AudioPath = Temp("ghost-a"),
        };

        Func<Task> act = () => muxer.MuxAsync(request);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task MuxAsync_FfmpegFails_RemovesPartialOutput_AndThrows()
    {
        var failing = new FailingRunner();
        var muxer = new MediaMuxer(failing, NullLogger<MediaMuxer>.Instance);
        MuxRequest request = BuildRequest(MediaContainer.Mp4, "h264", "aac") with { OutputPath = Temp("fail.mp4") };

        Func<Task> act = () => muxer.MuxAsync(request);

        await act.Should().ThrowAsync<FfmpegException>();
        File.Exists(request.OutputPath!).Should().BeFalse("a failed mux leaves no partial output");
    }

    private sealed class FailingRunner : IFfmpegRunner
    {
        public Task<FfmpegRunResult> RunAsync(
            IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            File.WriteAllText(arguments[^1], "partial");
            return Task.FromResult(new FfmpegRunResult(1, "boom"));
        }
    }

    // --- Real-ffmpeg integration -----------------------------------------------------------------

    [Fact]
    public async Task MuxAsync_RealFfmpeg_ProducesValidMp4_WithBothStreams_StreamCopied()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadMedia();
        using ServiceProvider provider = services.BuildServiceProvider();

        var runner = provider.GetRequiredService<IFfmpegRunner>();
        if (await provider.GetRequiredService<IFfmpegLocator>().LocateAsync() is null)
        {
            return; // ffmpeg not installed on this host.
        }

        string videoFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ts");
        if (!File.Exists(videoFixture))
        {
            return;
        }

        // Generate a short AAC audio track to pair with the H.264 video fixture.
        string audioPath = Temp("tone.m4a");
        FfmpegRunResult gen = await runner.RunAsync(
            ["-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "aac", audioPath]);
        gen.Succeeded.Should().BeTrue($"audio generation should succeed; stderr: {gen.StandardError}");

        var muxer = provider.GetRequiredService<IMediaMuxer>();
        MuxResult result = await muxer.MuxAsync(new MuxRequest
        {
            VideoPath = videoFixture,
            AudioPath = audioPath,
            PreferredContainer = MediaContainer.Mp4,
            VideoCodec = "h264",
            AudioCodec = "aac",
            OutputPath = Temp("muxed.mp4"),
        });

        result.Container.Should().Be(MediaContainer.Mp4);
        File.Exists(result.OutputPath).Should().BeTrue();

        // ffprobe must read it as a valid container with a stream-copied h264 video and aac audio
        // (one CSV line per stream, "codec_name,codec_type").
        string streams = await ProbeCodecsAsync(result.OutputPath);
        streams.Should().Contain("h264").And.Contain("video", "video is stream-copied unchanged");
        streams.Should().Contain("aac").And.Contain("audio", "audio is stream-copied unchanged");
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
        foreach (string path in _temp)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
