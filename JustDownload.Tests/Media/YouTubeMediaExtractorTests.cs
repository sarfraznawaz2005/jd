using FluentAssertions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.YouTube;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The YouTube extractor's honest, narrow best-effort scope (TASK-101, D3): only a fully bare, unciphered,
/// unthrottled <c>streamingData.formats</c> URL is ever accepted; a <c>signatureCipher</c>d format, a bare
/// URL that still needs its "n" throttling parameter resolved, or an unparseable/unfetchable page all
/// decline (return null) rather than guessing or faking a result. The ciphered and throttled fixtures here
/// are trimmed extracts of real YouTube watch pages captured during TASK-101's research (see the fixture
/// files) — reflecting the empirical finding that essentially no real YouTube video currently exposes a
/// usable format under this scope. The "synthetic" fixture is explicitly not real; it only proves the
/// parser's positive path is implemented correctly.
/// </summary>
public sealed class YouTubeMediaExtractorTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static YouTubeMediaExtractor Build(MapTransport transport) =>
        new(transport, NullLogger<YouTubeMediaExtractor>.Instance);

    private static MediaRequest Request(string url) => new() { Url = new Uri(url) };

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixturesDir, name));

    [Fact]
    public async Task TryExtractAsync_NonYouTubeUrl_ReturnsNull_WithoutFetching()
    {
        var transport = new MapTransport();

        MediaSource? source = await Build(transport).TryExtractAsync(Request("https://example.com/video.mp4"));

        source.Should().BeNull();
        transport.RequestedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_CipheredFormat_ReturnsNull()
    {
        var transport = new MapTransport().AddText(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", ReadFixture("youtube-ciphered.html"));

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));

        source.Should().BeNull("the only format present requires solving YouTube's signature cipher, which is out of scope");
    }

    [Fact]
    public async Task TryExtractAsync_BareUrlWithUnresolvedNParam_ReturnsNull()
    {
        var transport = new MapTransport().AddText(
            "https://www.youtube.com/watch?v=jNQXAC9IVRw", ReadFixture("youtube-throttled-n-param.html"));

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=jNQXAC9IVRw"));

        source.Should().BeNull("the bare URL still needs its \"n\" throttling parameter resolved, which is out of scope");
    }

    [Fact]
    public async Task TryExtractAsync_SyntheticUnciphered_ReturnsProgressiveSource()
    {
        var transport = new MapTransport().AddText(
            "https://www.youtube.com/watch?v=synthetic01", ReadFixture("youtube-unciphered-synthetic.html"));

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=synthetic01"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("youtube");
        source.Kind.Should().Be(MediaKind.Progressive);
        source.Url.ToString().Should().Contain("example-unciphered");
        source.SuggestedFileName.Should().Be("youtube-synthetic01");
    }

    [Fact]
    public async Task TryExtractAsync_ResolvesVideoId_FromYoutuBeShortLink()
    {
        var transport = new MapTransport().AddText(
            "https://www.youtube.com/watch?v=synthetic01", ReadFixture("youtube-unciphered-synthetic.html"));

        MediaSource? source = await Build(transport).TryExtractAsync(Request("https://youtu.be/synthetic01"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Progressive);
    }

    [Fact]
    public async Task TryExtractAsync_UnfetchableWatchPage_ReturnsNull()
    {
        // No body mapped for this URL → the fake transport 404s → decline gracefully.
        MediaSource? source = await Build(new MapTransport())
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=missing0001"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_PageWithoutPlayerResponse_ReturnsNull()
    {
        var transport = new MapTransport().AddText(
            "https://www.youtube.com/watch?v=notarealvid", "<html><body>not a real watch page</body></html>");

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request("https://www.youtube.com/watch?v=notarealvid"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_NoRecognisableVideoId_ReturnsNull_WithoutFetching()
    {
        var transport = new MapTransport();

        MediaSource? source = await Build(transport).TryExtractAsync(Request("https://www.youtube.com/feed/trending"));

        source.Should().BeNull();
        transport.RequestedUrls.Should().BeEmpty();
    }
}
