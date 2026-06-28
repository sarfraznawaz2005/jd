using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        MediaSource? result = await registry.ExtractAsync(Request("https://example/a.mp4"));

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

        MediaSource? result = await BuildRegistry(declines, matches).ExtractAsync(Request("https://x/y"));

        result!.ExtractorName.Should().Be("matches");
    }

    [Fact]
    public async Task ExtractAsync_UnknownMedia_ReturnsNull()
    {
        var declines = new StubExtractor("declines", 1, _ => null);

        MediaSource? result = await BuildRegistry(declines).ExtractAsync(Request("https://x/page.html"));

        result.Should().BeNull("unknown media degrades gracefully rather than throwing");
    }

    [Fact]
    public async Task ExtractAsync_ThrowingExtractor_IsSkipped_NotFatal()
    {
        var faulty = new StubExtractor("faulty", 1, _ => throw new InvalidOperationException("boom"));
        var good = new StubExtractor("good", 2, r => SourceFrom("good", r));

        MediaSource? result = await BuildRegistry(faulty, good).ExtractAsync(Request("https://x/a.mp4"));

        result!.ExtractorName.Should().Be("good", "one bad extractor must not break the chain");
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
        services.AddJustDownloadMedia();
        using ServiceProvider provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IMediaExtractorRegistry>();

        registry.Extractors.Should().Contain(e => e.Name == "progressive",
            "the generic extractor registers at startup (AC1)");
    }
}
