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

    /// <summary>True when <paramref name="args"/> carries <c>--cookies</c> immediately followed by
    /// <paramref name="expectedPath"/> — used to assert the cookie-file retry argv shape.</summary>
    private static bool HasCookieArg(IReadOnlyList<string> args, string expectedPath) =>
        args.Contains("--cookies") && args.Contains(expectedPath) &&
        args.ToList().IndexOf("--cookies") is int i && i + 1 < args.Count && args[i + 1] == expectedPath;

    private static ISettingsService SettingsWith(bool videoCaptureEnabled)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { VideoCaptureEnabled = videoCaptureEnabled });
        return settings;
    }

    private static ISettingsService SettingsWithCookies(
        bool videoCaptureEnabled, string? cookieFile = null, string? cookieBrowser = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings
        {
            VideoCaptureEnabled = videoCaptureEnabled,
            YtDlpCookieFilePath = cookieFile,
            YtDlpCookieBrowser = cookieBrowser,
        });
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
    public async Task TryExtractAsync_SingleMuxedFormat_MapsToProgressiveMediaSourceWithOneVariant()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"jNQXAC9IVRw","formats":[
              {"format_id":"18","url":"https://rr2.googlevideo.com/videoplayback?itag=18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"tbr":359.6}
            ]}
            """;
        runner.RunAsync(LocatedYtDlp.ExecutablePath, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=jNQXAC9IVRw"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("yt-dlp");
        source.Kind.Should().Be(MediaKind.Progressive);
        source.SuggestedFileName.Should().Be("ytdlp-jNQXAC9IVRw");
        source.Variants.Should().HaveCount(1);
        source.Variants[0].Id.Should().Be("https://rr2.googlevideo.com/videoplayback?itag=18");
        source.Variants[0].Height.Should().Be(360);
        source.Variants[0].Bandwidth.Should().Be(359_600);
        source.AudioVariants.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_MultipleMuxedFormats_MapsToMultipleProgressiveVariants()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360},
              {"format_id":"22","url":"https://cdn.example.com/v22","protocol":"https","vcodec":"avc1.640028","acodec":"mp4a.40.2","height":720}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Progressive);
        source.Variants.Should().HaveCount(2);
        source.Variants.Select(v => v.Height).Should().BeEquivalentTo([360, 720]);
    }

    [Fact]
    public async Task TryExtractAsync_VideoOnlyAndAudioOnlyFormats_MapsToSeparateStreamsWithBothVariantLists()
    {
        // Confirmed empirically (2026-07-02, real yt-dlp 2026.06.09 against a real YouTube video): this is
        // the common shape — only one low-resolution muxed format, every higher quality is video-only, and
        // audio is always separate. Mapping both lists lets VideoQualitySelector actually pick a real quality.
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"18","url":"https://cdn.example.com/muxed360","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360},
              {"format_id":"135","url":"https://cdn.example.com/v480","protocol":"https","vcodec":"avc1.4d401f","acodec":"none","height":480,"vbr":355.6},
              {"format_id":"298","url":"https://cdn.example.com/v720","protocol":"https","vcodec":"avc1.4d4020","acodec":"none","height":720,"vbr":1897.7},
              {"format_id":"140","url":"https://cdn.example.com/audio","protocol":"https","vcodec":"none","acodec":"mp4a.40.2","abr":129.5}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(
            MediaKind.SeparateStreams, "video-only + audio-only formats take priority over the lone low-res muxed one");
        source.Variants.Should().HaveCount(2, "the muxed-360p format is excluded once separate streams exist");
        source.Variants.Select(v => v.Height).Should().BeEquivalentTo([480, 720]);
        source.AudioVariants.Should().HaveCount(1);
        source.AudioVariants[0].Id.Should().Be("https://cdn.example.com/audio");
        source.AudioVariants[0].Bandwidth.Should().Be(129_500);
    }

    [Fact]
    public async Task TryExtractAsync_SameResolutionFormatsWithDifferentCodecsAndFps_MapsFpsAndCodecOntoEveryVariant()
    {
        // TASK-166: yt-dlp's raw formats commonly contain several distinct 720p renditions (H.264 vs VP9 vs
        // AV1, 30fps vs 60fps) — every one must map through with its own fps/codec, none dropped or merged.
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"136","url":"https://cdn.example.com/h264-30","protocol":"https","vcodec":"avc1.4d401f","acodec":"none","height":720,"vbr":1000,"fps":30},
              {"format_id":"247","url":"https://cdn.example.com/vp9-30","protocol":"https","vcodec":"vp9","acodec":"none","height":720,"vbr":600,"fps":30},
              {"format_id":"298","url":"https://cdn.example.com/h264-60","protocol":"https","vcodec":"avc1.4d4020","acodec":"none","height":720,"vbr":1900,"fps":60},
              {"format_id":"400","url":"https://cdn.example.com/av1-30","protocol":"https","vcodec":"av01.0.05M.08","acodec":"none","height":720,"vbr":500,"fps":30},
              {"format_id":"140","url":"https://cdn.example.com/audio","protocol":"https","vcodec":"none","acodec":"mp4a.40.2","abr":129.5}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        source!.Variants.Should().HaveCount(4, "no yt-dlp format entry is dropped or deduped, even at identical resolutions");
        source.Variants.Should().Contain(v => v.Id.EndsWith("h264-30", StringComparison.Ordinal) && v.Codec == "H.264" && v.Fps == 30);
        source.Variants.Should().Contain(v => v.Id.EndsWith("vp9-30", StringComparison.Ordinal) && v.Codec == "VP9" && v.Fps == 30);
        source.Variants.Should().Contain(v => v.Id.EndsWith("h264-60", StringComparison.Ordinal) && v.Codec == "H.264" && v.Fps == 60);
        source.Variants.Should().Contain(v => v.Id.EndsWith("av1-30", StringComparison.Ordinal) && v.Codec == "AV1" && v.Fps == 30);
    }

    [Fact]
    public async Task TryExtractAsync_UnrecognizedVcodec_FallsBackToTheRawVcodecString()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"1","url":"https://cdn.example.com/theora","protocol":"https","vcodec":"theora","acodec":"vorbis","height":480}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        source!.Variants.Should().ContainSingle();
        source.Variants[0].Codec.Should().Be("theora");
        source.Variants[0].Fps.Should().BeNull("this format never reported an fps");
    }

    [Fact]
    public async Task TryExtractAsync_NoFpsOrVcodecReported_LeavesThemNull()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"jNQXAC9IVRw","formats":[
              {"format_id":"18","url":"https://rr2.googlevideo.com/videoplayback?itag=18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"tbr":359.6}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=jNQXAC9IVRw"));

        source.Should().NotBeNull();
        source!.Variants[0].Fps.Should().BeNull("this fixture format never carried an fps field");
        source.Variants[0].Codec.Should().Be("H.264", "avc1.* still maps to a friendly label even without fps");
    }

    [Fact]
    public async Task TryExtractAsync_TitlePresent_UsesTheSanitizedTitleAsSuggestedFileName()
    {
        // Steering feedback: downloaded videos were saved under the opaque "ytdlp-{id}" name instead of the
        // real title. yt-dlp's --dump-json already reports "title"; use it (sanitized) when present.
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"jNQXAC9IVRw","title":"Me at the zoo: Part 1?","formats":[
              {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=jNQXAC9IVRw"));

        source.Should().NotBeNull();
        source!.SuggestedFileName.Should().Be(
            "Me at the zoo_ Part 1_", "characters invalid in a file name (: and ?) are sanitized to _");
    }

    [Fact]
    public async Task TryExtractAsync_M3U8Format_MapsToHlsMediaSourceWithVariant()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"hls-720","url":"https://cdn.example.com/720.m3u8","protocol":"m3u8_native","height":720}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://example.com/live"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls, "an HLS-protocol format hands off to the existing HLS pipeline");
        source.Variants.Should().ContainSingle();
        source.Variants[0].Id.Should().Be("https://cdn.example.com/720.m3u8");
        source.Variants[0].Height.Should().Be(720);
    }

    [Fact]
    public async Task TryExtractAsync_MalformedFormatEntries_AreSkipped_UsableOnesStillMap()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"no-url","url":"","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360},
              {"format_id":"no-height","url":"https://cdn.example.com/no-height","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2"},
              {"format_id":"storyboard","url":"https://cdn.example.com/sb0.jpg","protocol":"mhtml","vcodec":"none","acodec":"none","height":90},
              {"format_id":"dash-fragmented","url":"https://cdn.example.com/frag","protocol":"http_dash_segments","vcodec":"avc1.4d401f","acodec":"none","height":480},
              {"format_id":"18","url":"https://cdn.example.com/muxed360","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull("the one well-formed, directly-downloadable muxed format is still usable");
        source!.Kind.Should().Be(MediaKind.Progressive);
        source.Variants.Should().ContainSingle();
        source.Variants[0].Id.Should().Be("https://cdn.example.com/muxed360");
    }

    [Fact]
    public async Task TryExtractAsync_OnlyUnusableFormats_ReportsWhyInsteadOfDecliningSilently()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"sb0","url":"https://cdn.example.com/sb0.jpg","protocol":"mhtml","vcodec":"none","acodec":"none","height":90},
              {"format_id":"dash-fragmented","url":"https://cdn.example.com/frag","protocol":"http_dash_segments","vcodec":"avc1.4d401f","acodec":"none","height":480}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Contain("no downloadable format",
                "yt-dlp did look at the URL, so the user is told why rather than shown a generic 'no media'");
    }

    [Fact]
    public async Task TryExtractAsync_NoFSelectorPassed_ArgumentsOmitDashF()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        await Build(settings, locator, runner).TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        await runner.Received(1).RunAsync(
            LocatedYtDlp.ExecutablePath,
            Arg.Is<IReadOnlyList<string>>(args =>
                !args.Contains("-f") && !args.Contains("best") &&
                args.Contains("--dump-json") && args[args.Count - 1] == "https://www.youtube.com/watch?v=abc"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"id":"x","url":""}""")]
    public async Task TryExtractAsync_MalformedOrEmptyOutput_ReportsUnreadableOutput(string stdout)
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, stdout, string.Empty));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        await act.Should().ThrowAsync<MediaExtractionFailedException>();
    }

    [Fact]
    public async Task TryExtractAsync_NonZeroExit_SurfacesTheRealStderrReason()
    {
        // The user's actual complaint: this reason was only ever logged at Debug, so with the default
        // Error log level the only way to see it was running yt-dlp by hand in a shell.
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(
                1,
                string.Empty,
                "WARNING: something noisy\nERROR: [youtube] abc: Sign in to confirm you're not a bot"));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Be("[youtube] abc: Sign in to confirm you're not a bot",
                "the last ERROR line is the real cause; the 'ERROR:' prefix is noise for a dialog");
    }

    [Fact]
    public async Task TryExtractAsync_RunnerThrowsYtDlpException_IsReportedAsAFailureWithTheReason()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<YtDlpRunResult>>(_ => throw new YtDlpException("Failed to start yt-dlp."));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc12345678"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Contain("Failed to start yt-dlp.");
    }

    [Fact]
    public void Priority_IsIntMaxValue_RunsStrictlyLast()
    {
        var extractor = Build(
            SettingsWith(false), Substitute.For<IYtDlpLocator>(), Substitute.For<IYtDlpRunner>());

        extractor.Priority.Should().Be(int.MaxValue);
    }

    [Fact]
    public async Task TryExtractAsync_NoCookieSetting_InvokesYtDlpWithoutCookiesArg()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        const string json = """
            {"id":"abc","formats":[
              {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
            ]}
            """;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(0, json, string.Empty));

        await Build(settings, locator, runner).TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        await runner.Received(1).RunAsync(
            LocatedYtDlp.ExecutablePath,
            Arg.Is<IReadOnlyList<string>>(args => !args.Contains("--cookies") && !args.Contains("--cookies-from-browser")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_CookiePathSet_AndBotError_RetriesWithCookiesArg()
    {
        ISettingsService settings = SettingsWithCookies(videoCaptureEnabled: true, cookieFile: "C:/cookies.txt");
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        // First call: bot-detection failure. Second (retry): succeeds.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(
                    1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"),
                _ => new YtDlpRunResult(0, """
                    {"id":"abc","formats":[
                      {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
                    ]}
                    """, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull("the cookie retry dislodged the bot challenge");
        source!.Variants.Should().ContainSingle();
        // Exactly one retry: a second invocation carrying the cookie args.
        await runner.Received(2).RunAsync(
            LocatedYtDlp.ExecutablePath, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            LocatedYtDlp.ExecutablePath,
            Arg.Is<IReadOnlyList<string>>(args => HasCookieArg(args, "C:/cookies.txt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_NonBotError_DoesNotRetry_AndSurfacesTheReason()
    {
        ISettingsService settings = SettingsWithCookies(videoCaptureEnabled: true, cookieFile: "C:/cookies.txt");
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        // A non-bot failure (unsupported URL) must NOT trigger a cookie retry even when cookies are set.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(1, string.Empty, "ERROR: [youtube] abc: Unsupported URL"));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Be("[youtube] abc: Unsupported URL");
        await runner.Received(1).RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_RetrySucceeds_ReturnsTheResult()
    {
        ISettingsService settings = SettingsWithCookies(videoCaptureEnabled: true, cookieFile: "C:/c.txt");
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(1, string.Empty, "ERROR: HTTP Error 429: Too Many Requests"),
                _ => new YtDlpRunResult(0, """
                    {"id":"abc","formats":[
                      {"format_id":"22","url":"https://cdn.example.com/v22","protocol":"https","vcodec":"avc1.640028","acodec":"mp4a.40.2","height":720}
                    ]}
                    """, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Progressive);
        source.Variants.Should().ContainSingle().Which.Height.Should().Be(720);
    }

    [Fact]
    public async Task TryExtractAsync_RetryAlsoFails_SurfacesRetryReason_StillOnlyOneRetry()
    {
        ISettingsService settings = SettingsWithCookies(videoCaptureEnabled: true, cookieFile: "C:/c.txt");
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        // Both the initial probe and the cookie retry hit the bot wall — the retry's own reason wins.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"),
                _ => new YtDlpRunResult(1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"));

        Func<Task<MediaSource?>> act = () => Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Be("[youtube] abc: Sign in to confirm you're not a bot");
        await runner.Received(2).RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_CookieBrowserSet_AndBotError_RetriesWithCookiesFromBrowserArg()
    {
        // Both cookie sources may be set; the browser variant is exercised separately from the file one.
        ISettingsService settings = SettingsWithCookies(videoCaptureEnabled: true, cookieBrowser: "chrome");
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(1, string.Empty, "ERROR: Please use --cookies-from-browser"),
                _ => new YtDlpRunResult(0, """
                    {"id":"abc","formats":[
                      {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
                    ]}
                    """, string.Empty));

        MediaSource? source = await Build(settings, locator, runner)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull();
        await runner.Received(1).RunAsync(
            LocatedYtDlp.ExecutablePath,
            Arg.Is<IReadOnlyList<string>>(args =>
                args.Contains("--cookies-from-browser") && args.Contains("chrome")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_NoCookieSetting_BotError_AutoDetectsBrowser_AndRetriesWithIt()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(
                    1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"),
                _ => new YtDlpRunResult(0, """
                    {"id":"abc","formats":[
                      {"format_id":"18","url":"https://cdn.example.com/v18","protocol":"https","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}
                    ]}
                    """, string.Empty));
        YtDlpMediaExtractor extractor = Build(settings, locator, runner);
        extractor.BrowserCookieStoreExists = name => name == "edge";

        MediaSource? source = await extractor.TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        source.Should().NotBeNull("the zero-config auto-detected browser cookies dislodged the bot challenge");
        await runner.Received(1).RunAsync(
            LocatedYtDlp.ExecutablePath,
            Arg.Is<IReadOnlyList<string>>(args =>
                args.Contains("--cookies-from-browser") && args.Contains("edge")),
            Arg.Any<CancellationToken>());
        await runner.Received(2).RunAsync(
            LocatedYtDlp.ExecutablePath, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_NoCookieSetting_NoBrowserDetected_DoesNotRetry_AndSurfacesBotReason()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new YtDlpRunResult(
                1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"));
        YtDlpMediaExtractor extractor = Build(settings, locator, runner);
        extractor.BrowserCookieStoreExists = _ => false;

        Func<Task<MediaSource?>> act = () => extractor.TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Be("[youtube] abc: Sign in to confirm you're not a bot");
        await runner.Received(1).RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryExtractAsync_AutoDetectedRetryFails_SurfacesOriginalBotReason_NotTheCookieReason()
    {
        ISettingsService settings = SettingsWith(videoCaptureEnabled: true);
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(LocatedYtDlp);
        var runner = Substitute.For<IYtDlpRunner>();
        // The guessed browser's cookies failed for their own reason — the user should still see the wall.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new YtDlpRunResult(
                    1, string.Empty, "ERROR: [youtube] abc: Sign in to confirm you're not a bot"),
                _ => new YtDlpRunResult(
                    1, string.Empty, "ERROR: could not copy edge cookie database"));
        YtDlpMediaExtractor extractor = Build(settings, locator, runner);
        extractor.BrowserCookieStoreExists = name => name == "edge";

        Func<Task<MediaSource?>> act = () => extractor.TryExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        (await act.Should().ThrowAsync<MediaExtractionFailedException>())
            .Which.Message.Should().Be("[youtube] abc: Sign in to confirm you're not a bot",
                "auto-detection is a best-effort guess; its own failure must not replace the real reason");
        await runner.Received(2).RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
