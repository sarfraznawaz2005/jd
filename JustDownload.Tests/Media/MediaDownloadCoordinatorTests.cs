using FluentAssertions;
using JustDownload.Core.Downloading;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Dash;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Media download orchestration (TASK-154/102): the coordinator drives the separate-stream downloader and the
/// muxer for SeparateStreams (proven here with substitutes — no ffmpeg), the DASH segment downloader +
/// concatenator for Dash, and surfaces a stream failure. HLS is covered end-to-end by the manager test; the
/// DASH segmented path is also covered end-to-end against a live loopback server (see DashLoopbackTests).
/// </summary>
public sealed class MediaDownloadCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-coord-" + Guid.NewGuid().ToString("N"));

    public MediaDownloadCoordinatorTests() => Directory.CreateDirectory(_dir);

    private static StreamOutcome Ok(StreamRole role, string path, long bytes) => new()
    {
        Role = role,
        DestinationPath = path,
        Succeeded = true,
        Result = new DownloadResult
        {
            TotalBytes = bytes,
            FinalUri = new Uri("https://x.example/s"),
            FileName = Path.GetFileName(path),
            SingleConnection = true,
            InitialSegments = 1,
            Steals = 0,
        },
    };

    private static MediaDownloadCoordinator Build(ISeparateStreamDownloader sep, IMediaMuxer mux) =>
        new(Substitute.For<IHlsDownloader>(), Substitute.For<IHlsConcatenator>(), sep,
            Substitute.For<IDashSegmentDownloader>(), mux);

    private static MediaDownloadCoordinator BuildDash(
        IDashSegmentDownloader dash, IHlsConcatenator concat, IMediaMuxer mux) =>
        new(Substitute.For<IHlsDownloader>(), concat, Substitute.For<ISeparateStreamDownloader>(), dash, mux);

    [Fact]
    public async Task SeparateStreams_DownloadsBothStreams_ThenMuxes_AndSumsBytes()
    {
        var sep = Substitute.For<ISeparateStreamDownloader>();
        sep.DownloadAsync(Arg.Any<StreamDownloadRequest>(), Arg.Any<StreamDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SeparateStreamResult(
                Ok(StreamRole.Video, Path.Combine(_dir, "work", "video.stream"), 7000),
                Ok(StreamRole.Audio, Path.Combine(_dir, "work", "audio.stream"), 3000)));
        var mux = Substitute.For<IMediaMuxer>();
        mux.MuxAsync(Arg.Any<MuxRequest>(), Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxResult(Path.Combine(_dir, "out.mkv"), MediaContainer.Mkv));

        MediaDownloadOutcome outcome = await Build(sep, mux).DownloadAsync(new MediaDownloadRequest
        {
            Kind = MediaKind.SeparateStreams,
            MediaUrl = new Uri("https://x.example/video"),
            AudioUrl = new Uri("https://x.example/audio"),
            Container = MediaContainer.Mkv,
            OutputPath = Path.Combine(_dir, "out.mkv"),
            WorkingDirectory = Path.Combine(_dir, "work"),
        });

        outcome.TotalBytes.Should().Be(10000);
        await mux.Received(1).MuxAsync(
            Arg.Is<MuxRequest>(m => m.OutputPath == Path.Combine(_dir, "out.mkv") && m.PreferredContainer == MediaContainer.Mkv),
            Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeparateStreams_OneStreamFails_Throws_AndDoesNotMux()
    {
        var sep = Substitute.For<ISeparateStreamDownloader>();
        sep.DownloadAsync(Arg.Any<StreamDownloadRequest>(), Arg.Any<StreamDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SeparateStreamResult(
                new StreamOutcome
                {
                    Role = StreamRole.Video,
                    DestinationPath = "v",
                    Succeeded = false,
                    Error = new InvalidOperationException("video failed"),
                },
                Ok(StreamRole.Audio, Path.Combine(_dir, "a"), 100)));
        var mux = Substitute.For<IMediaMuxer>();

        Func<Task> act = () => Build(sep, mux).DownloadAsync(new MediaDownloadRequest
        {
            Kind = MediaKind.SeparateStreams,
            MediaUrl = new Uri("https://x.example/video"),
            AudioUrl = new Uri("https://x.example/audio"),
            OutputPath = Path.Combine(_dir, "out.mkv"),
            WorkingDirectory = Path.Combine(_dir, "work"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("video failed");
        await mux.DidNotReceive().MuxAsync(Arg.Any<MuxRequest>(), Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeparateStreams_WithoutAudioUrl_Throws()
    {
        Func<Task> act = () => Build(Substitute.For<ISeparateStreamDownloader>(), Substitute.For<IMediaMuxer>())
            .DownloadAsync(new MediaDownloadRequest
            {
                Kind = MediaKind.SeparateStreams,
                MediaUrl = new Uri("https://x.example/video"),
                AudioUrl = null,
                OutputPath = Path.Combine(_dir, "out.mkv"),
                WorkingDirectory = Path.Combine(_dir, "work"),
            });

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // --- Dash (TASK-102): segment download + concat, reusing IHlsConcatenator, then mux --------------

    [Fact]
    public async Task Dash_WithAudio_DownloadsBothRepresentations_ConcatenatesAndMuxes()
    {
        var videoUri = new Uri("https://x.example/m.mpd#dash-rep=v0");
        var audioUri = new Uri("https://x.example/m.mpd#dash-rep=a0");
        var dash = Substitute.For<IDashSegmentDownloader>();
        dash.DownloadAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(),
                Arg.Any<IProgress<DashSegmentProgress>?>(), Arg.Any<CancellationToken>())
            // Uri equality ignores the fragment (RFC 3986 §5.3), so disambiguate by the raw fragment string.
            .Returns(ci => ci.ArgAt<Uri>(0).Fragment == videoUri.Fragment
                ? new DashSegmentDownloadResult(["init", "seg0", "seg1"], 6000)
                : new DashSegmentDownloadResult(["aseg0"], 2000));

        var concat = Substitute.For<IHlsConcatenator>();
        concat.ConcatenateAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.ArgAt<string>(1)));

        var mux = Substitute.For<IMediaMuxer>();
        mux.MuxAsync(Arg.Any<MuxRequest>(), Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxResult(Path.Combine(_dir, "out.mkv"), MediaContainer.Mkv));

        MediaDownloadOutcome outcome = await BuildDash(dash, concat, mux).DownloadAsync(new MediaDownloadRequest
        {
            Kind = MediaKind.Dash,
            MediaUrl = videoUri,
            AudioUrl = audioUri,
            Container = MediaContainer.Mkv,
            OutputPath = Path.Combine(_dir, "out.mkv"),
            WorkingDirectory = Path.Combine(_dir, "work"),
        });

        outcome.TotalBytes.Should().Be(8000);
        await mux.Received(1).MuxAsync(
            Arg.Is<MuxRequest>(m =>
                m.VideoPath == Path.Combine(_dir, "work", "video.stream") &&
                m.AudioPath == Path.Combine(_dir, "work", "audio.stream")),
            Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dash_WithoutAudioUrl_ConcatenatesVideoOnly_AndSkipsMux()
    {
        var dash = Substitute.For<IDashSegmentDownloader>();
        dash.DownloadAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(),
                Arg.Any<IProgress<DashSegmentProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new DashSegmentDownloadResult(["seg0"], 500));

        var concat = Substitute.For<IHlsConcatenator>();
        concat.ConcatenateAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string outputPath = ci.ArgAt<string>(1);
                File.WriteAllText(outputPath, "video-only");
                return Task.FromResult(outputPath);
            });
        var mux = Substitute.For<IMediaMuxer>();

        string finalOutput = Path.Combine(_dir, "out.mkv");
        MediaDownloadOutcome outcome = await BuildDash(dash, concat, mux).DownloadAsync(new MediaDownloadRequest
        {
            Kind = MediaKind.Dash,
            MediaUrl = new Uri("https://x.example/m.mpd#dash-rep=v0"),
            AudioUrl = null,
            OutputPath = finalOutput,
            WorkingDirectory = Path.Combine(_dir, "work"),
        });

        outcome.TotalBytes.Should().Be(500);
        File.Exists(finalOutput).Should().BeTrue("no separate audio representation — the concatenated video is the output");
        await mux.DidNotReceive().MuxAsync(Arg.Any<MuxRequest>(), Arg.Any<IProgress<FfmpegProgress>?>(), Arg.Any<CancellationToken>());
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
