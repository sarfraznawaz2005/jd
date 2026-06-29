using FluentAssertions;
using JustDownload.Core.Downloading;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Media download orchestration (TASK-154): the coordinator drives the separate-stream downloader and the
/// muxer for SeparateStreams/DASH (proven here with substitutes — no ffmpeg), and surfaces a stream failure.
/// HLS is covered end-to-end by the manager test.
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
        new(Substitute.For<IHlsDownloader>(), Substitute.For<IHlsConcatenator>(), sep, mux);

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
