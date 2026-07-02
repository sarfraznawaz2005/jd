using FluentAssertions;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.YtDlp;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The optional yt-dlp fallback extractor (TASK-163, D3): the master toggle and locator gates that must
/// never spawn a subprocess (AC0), and the JSON-parsing/mapping/fallback-gating logic against a mocked
/// <see cref="IYtDlpRunner"/> — no live network calls and no real yt-dlp binary needed for this suite (AC2).
/// Real end-to-end behaviour against a real yt-dlp binary and real YouTube/Facebook/Twitter-X URLs was
/// verified separately during development (see the task's implementation notes) — that is a one-time
/// empirical check, not part of the automated/CI suite, per the task's own scoping.
/// </summary>
public sealed class YtDlpMediaExtractorTests
{
    private static readonly YtDlpInfo LocatedYtDlp = new("/vendor/yt-dlp", "2026.06.09");

    private static MediaRequest Request(string url) => new() { Url = new Uri(url) };

    private static ISettingsService SettingsWith(bool videoCaptureEnabled)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { VideoCaptureEnabled = videoCaptureEnabled });
        return settings;
    }

    private static YtDlpMediaExtractor Build(
        ISettingsService settings, IYtDlpLocator locator, IYtDlpRunner runner) =>
        new(settings, locator, runner, NullLogger<YtDlpMediaExtractor>.Instance);

    [Fact]
    public async Task TryExtractAsync_ToggleOff_ReturnsNull_WithoutCallingLocatorOrRunner()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: false);
        var locator = Substitute.For<IYtDlpLocator>();
        var runner = Substitute.For<IYtDlpRunner>();

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        source.Should().BeNull("the master toggle gates the fallback off by default (AC0)");
        await locator.DidNotReceive().LocateAsync(Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_ToggleOn_NotProvisioned_ReturnsNull_WithoutSpawningProcess()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var runner = Substitute.For<IYtDlpRunner>();

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        source.Should().BeNull("yt-dlp is not provisioned; provisioning is an explicit Settings action, never implicit (AC0)");
        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_ValidDumpJson_MapsToProgressiveMediaSource()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"jNQXAC9IVRw","ext":"mp4","url":"https://rr2.googlevideo.com/videoplayback?itag=18","protocol":"https","format_id":"18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
            """;
        runner.RunAsync(LocatedYtDlp.ExecutablePath, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=jNQXAC9IVRw"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("yt-dlp");
        source.Kind.Should().Be(MediaKind.Progressive);
        source.Url.Should().Be(new Uri("https://rr2.googlevideo.com/videoplayback?itag=18"));
        source.SuggestedFileName.Should().Be("ytdlp-jNQXAC9IVRw");
    }

    [Fact]
    public async Task TryExtractAsync_M3U8Format_MapsToHlsMediaSource()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """{"id":"abc","url":"https://cdn.example.com/master.m3u8","protocol":"m3u8_native"}""";
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://example.com/live"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls, "an HLS-protocol format hands off to the existing HLS pipeline");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"id":"x","url":""}""")]
    public async Task TryExtractAsync_MalformedOrEmptyOutput_ReturnsNull_DoesNotThrow(string stdout)
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, stdout, string.Empty));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_NonZeroExit_ReturnsNull_DoesNotThrow()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(1, string.Empty, "ERROR: Unsupported URL"));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_RunnerThrowsYtDlpException_ReturnsNull_DoesNotPropagate()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<YtDlpRunResult>>(_ => throw new YtDlpException("Failed to start yt-dlp."));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        source.Should().BeNull();
    }

    [Fact]
    public void Priority_IsIntMaxValue_RunsStrictlyLast()
    {
        var extractor = Build(
            SettingsWith(false), Substitute.For<IYtDlpLocator>(), Substitute.For<IYtDlpRunner>());

        extractor.Priority.Should().Be(int.MaxValue);
    }
}
