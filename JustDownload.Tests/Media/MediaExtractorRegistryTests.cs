using System.Net.Sockets;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The pluggable extractor registry (TASK-036): priority ordering (AC0), the generic extractors wired at
/// startup through the composition root (AC1), and graceful degradation when nothing recognises a URL or
/// an extractor throws (AC2).
/// </summary>
public sealed class MediaExtractorRegistryTests
{
    private static MediaRequest Request(string url, string? contentType = null) =>
        new() { Url = new Uri(url), ContentType = contentType };

    private sealed class StubExtractor : IMediaExtractor
    {
        private readonly Func<MediaRequest, MediaSource?> _handle;

        public StubExtractor(string name, int priority, Func<MediaRequest, MediaSource?> handle)
        {
            Name = name;
            Priority = priority;
            _handle = handle;
        }

        public string Name { get; }

        public int Priority { get; }

        public Task<MediaSource?> TryExtractAsync(MediaRequest request, CancellationToken ct = default) =>
            Task.FromResult(_handle(request));
    }

    /// <summary>
    /// Stands in for a third party's own <see cref="IMediaExtractor"/> implementation living outside
    /// JustDownload.Core (TASK-150) — a parameterless class so it can be registered via
    /// <see cref="ThirdPartyMediaExtractorExtensions.AddThirdPartyMediaExtractor{TExtractor}"/>, exactly as
    /// a real third party would register their own type.
    /// </summary>
    private sealed class AcmeClipsExtractor : IMediaExtractor
    {
        public string Name => "acme-clips";

        // The open 100-999 band: after protocol-level HLS (100)/DASH (110), before Progressive's catch-all (1000).
        public int Priority => 500;

        public Task<MediaSource?> TryExtractAsync(MediaRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Url.Host == "acme-clips.example" ? SourceFrom(Name, request) : null);
    }

    private static MediaSource SourceFrom(string extractorName, MediaRequest request) => new()
    {
        ExtractorName = extractorName,
        Kind = MediaKind.Progressive,
        Url = request.Url,
    };

    private static MediaExtractorRegistry BuildRegistry(params IMediaExtractor[] extractors) =>
        new(extractors, NullLogger<MediaExtractorRegistry>.Instance);

    [Fact]
    public async Task ExtractAsync_TriesExtractorsInAscendingPriority_FirstMatchWins()
    {
        var calls = new List<string>();
        var low = new StubExtractor("low", 10, r => { calls.Add("low"); return SourceFrom("low", r); });
        var high = new StubExtractor("high", 100, r => { calls.Add("high"); return SourceFrom("high", r); });

        // Registered out of order; the registry must order by priority and stop at the first match.
        MediaExtractorRegistry registry = BuildRegistry(high, low);

        MediaSource? result = (await registry.ExtractAsync(Request("https://example/a.mp4"))).Source;

        result.Should().NotBeNull();
        result!.ExtractorName.Should().Be("low", "the lower-priority value runs first and matches");
        calls.Should().ContainSingle().Which.Should().Be("low", "a match short-circuits later extractors");
        registry.Extractors.Select(e => e.Name).Should().ContainInOrder("low", "high");
    }

    [Fact]
    public async Task ExtractAsync_SkipsNonMatchingExtractors()
    {
        var declines = new StubExtractor("declines", 1, _ => null);
        var matches = new StubExtractor("matches", 2, r => SourceFrom("matches", r));

        MediaSource? result = (await BuildRegistry(declines, matches).ExtractAsync(Request("https://x/y"))).Source;

        result!.ExtractorName.Should().Be("matches");
    }

    [Fact]
    public async Task ExtractAsync_UnknownMedia_ReturnsNull()
    {
        var declines = new StubExtractor("declines", 1, _ => null);

        MediaExtractionResult result = await BuildRegistry(declines).ExtractAsync(Request("https://x/page.html"));

        result.Source.Should().BeNull("unknown media degrades gracefully rather than throwing");
        result.Attempts.Should().ContainSingle()
            .Which.Outcome.Should().Be(MediaExtractionOutcome.Declined, "a plain decline is not a failure");
    }

    [Fact]
    public async Task ExtractAsync_ThrowingExtractor_IsSkipped_NotFatal()
    {
        var faulty = new StubExtractor("faulty", 1, _ => throw new InvalidOperationException("boom"));
        var good = new StubExtractor("good", 2, r => SourceFrom("good", r));

        MediaExtractionResult result = await BuildRegistry(faulty, good).ExtractAsync(Request("https://x/a.mp4"));

        result.Source!.ExtractorName.Should().Be("good", "one bad extractor must not break the chain");
        result.Attempts[0].Outcome.Should().Be(MediaExtractionOutcome.Failed, "the throw is reported, not swallowed");
        result.Attempts[0].Reason.Should().Contain("boom");
        result.Attempts[1].Outcome.Should().Be(MediaExtractionOutcome.Accepted);
    }

    [Fact]
    public async Task ExtractAsync_TransportFailure_IsReportedAsNetworkFailure_NotADecline()
    {
        // A DNS/connectivity failure says nothing about whether media exists at the URL, so it must never
        // reach the user as "couldn't find downloadable media" (the whole point of the attempt vocabulary).
        var unreachable = new StubExtractor(
            "facebook", 1, _ => throw new HttpRequestException(
                "No such host is known.", new SocketException(11001)));

        MediaExtractionResult result =
            await BuildRegistry(unreachable).ExtractAsync(Request("https://www.facebook.com/reel/1"));

        result.Source.Should().BeNull();
        result.Attempts.Should().ContainSingle();
        result.Attempts[0].Outcome.Should().Be(MediaExtractionOutcome.NetworkFailure);
        result.Attempts[0].ExtractorName.Should().Be("facebook");
        result.Attempts[0].Reason.Should().Contain("No such host is known.");
    }

    [Fact]
    public async Task ExtractAsync_ExtractorReportingItsOwnReason_IsReportedAsFailedWithThatReason()
    {
        var recognised = new StubExtractor(
            "yt-dlp", 1, _ => throw new MediaExtractionFailedException("Sign in to confirm you're not a bot"));

        MediaExtractionResult result =
            await BuildRegistry(recognised).ExtractAsync(Request("https://www.youtube.com/watch?v=abc"));

        result.Source.Should().BeNull();
        result.Attempts.Should().ContainSingle();
        result.Attempts[0].Outcome.Should().Be(MediaExtractionOutcome.Failed);
        result.Attempts[0].Reason.Should().Be("Sign in to confirm you're not a bot");
    }

    [Fact]
    public async Task ExtractAsync_ReasonContainingASignedUrl_IsRedactedBeforeItCanBeShown()
    {
        // CLAUDE.md §5: user-facing messages must never carry signed-URL query strings, tokens or cookies.
        const string signed =
            "https://video.twimg.com/pl/x.m3u8?Policy=eyJTdGF0…&Signature=SECRET123&Key-Pair-Id=APKA";
        var leaky = new StubExtractor(
            "leaky", 1, _ => throw new MediaExtractionFailedException($"HTTP 403 fetching {signed}"));

        MediaExtractionResult result = await BuildRegistry(leaky).ExtractAsync(Request("https://x/a.m3u8"));

        string reason = result.Attempts[0].Reason!;
        reason.Should().NotContain("Signature=").And.NotContain("SECRET123").And.NotContain("Key-Pair-Id");
        reason.Should().Contain("https://video.twimg.com/pl/x.m3u8", "the URL itself is still useful context");
    }

    [Fact]
    public async Task ExtractAsync_MultiLineReason_IsCollapsedAndTruncated()
    {
        var noisy = new StubExtractor(
            "noisy", 1, _ => throw new MediaExtractionFailedException("first line\n" + new string('x', 400)));

        MediaExtractionResult result = await BuildRegistry(noisy).ExtractAsync(Request("https://x/a.mp4"));

        string reason = result.Attempts[0].Reason!;
        reason.Should().NotContain("\n");
        reason.Length.Should().BeLessThan(200, "the dialog footer cannot carry an unbounded error dump");
    }

    [Fact]
    public async Task ExtractAsync_PropagatesCancellation()
    {
        var any = new StubExtractor("any", 1, r => SourceFrom("any", r));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => BuildRegistry(any).ExtractAsync(Request("https://x/a.mp4"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("https://cdn.example.com/clip.mp4", true)]
    [InlineData("https://cdn.example.com/song.mp3", true)]
    [InlineData("https://cdn.example.com/movie.MKV", true)]
    [InlineData("https://cdn.example.com/playlist.m3u8", false)]
    [InlineData("https://cdn.example.com/manifest.mpd", false)]
    [InlineData("https://example.com/article", false)]
    public async Task ProgressiveExtractor_RecognisesMediaByExtension(string url, bool expectMatch)
    {
        var extractor = new ProgressiveMediaExtractor();

        MediaSource? result = await extractor.TryExtractAsync(Request(url));

        (result is not null).Should().Be(expectMatch);
        if (expectMatch)
        {
            result!.Kind.Should().Be(MediaKind.Progressive);
        }
    }

    [Fact]
    public async Task ProgressiveExtractor_RecognisesMediaByContentType()
    {
        var extractor = new ProgressiveMediaExtractor();

        MediaSource? result = await extractor.TryExtractAsync(
            Request("https://example.com/download?id=42", contentType: "video/mp4"));

        result.Should().NotBeNull();
        result!.Kind.Should().Be(MediaKind.Progressive);
    }

    [Fact]
    public async Task ProgressiveExtractor_DerivesSuggestedFileName()
    {
        var extractor = new ProgressiveMediaExtractor();

        MediaSource? result = await extractor.TryExtractAsync(Request("https://cdn/My%20Clip.mp4"));

        result!.SuggestedFileName.Should().Be("My Clip.mp4");
    }

    [Fact]
    public void CompositionRoot_RegistersRegistry_WithGenericExtractor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport(); // HLS/DASH extractors depend on ITransport

        // The yt-dlp fallback extractor (TASK-163) needs ISettingsService; substitute a no-DB fake with the
        // (default, off) video-capture toggle rather than pulling in the full SQLite-backed settings store.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);

        services.AddJustDownloadMedia();
        using ServiceProvider provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IMediaExtractorRegistry>();

        registry.Extractors.Should().Contain(e => e.Name == "progressive",
            "the generic extractor registers at startup (AC1)");
        registry.Extractors.Should().Contain(e => e.Name == "yt-dlp",
            "the yt-dlp fallback extractor registers at startup too (TASK-163)");
        registry.Extractors[^1].Name.Should().Be("yt-dlp",
            "yt-dlp must run strictly last, after every in-house extractor including Progressive's catch-all");
    }

    [Fact]
    public async Task CompositionRoot_RegistersThirdPartyExtractor_WithoutModifyingCoreCode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport(); // HLS/DASH extractors depend on ITransport

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);

        // A third party registers their own extractor via the public seam, never touching
        // ServiceCollectionExtensions.cs — this is the entire proof for TASK-150's acceptance criterion.
        services.AddJustDownloadMedia();
        services.AddThirdPartyMediaExtractor<AcmeClipsExtractor>();
        using ServiceProvider provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IMediaExtractorRegistry>();

        // Priority 500 sits in the open band: after HLS (100)/DASH (110) but before Progressive's
        // catch-all (1000) and yt-dlp's last-resort (int.MaxValue).
        registry.Extractors.Select(e => e.Name).Should().ContainInOrder("hls", "dash", "acme-clips", "progressive", "yt-dlp");

        MediaSource? result = (await registry.ExtractAsync(Request("https://acme-clips.example/watch?v=1"))).Source;

        result.Should().NotBeNull();
        result!.ExtractorName.Should().Be("acme-clips",
            "the third-party extractor was registered without touching ServiceCollectionExtensions.cs and is actually dispatched to");
    }
}
