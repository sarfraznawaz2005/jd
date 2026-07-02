using FluentAssertions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Facebook;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The Facebook extractor (TASK-101, D3): a video id is read directly from the URL when present, or
/// (for opaque short links) resolved by fetching the page and reading its embedded <c>video_id</c>; either
/// way, the actual playable URL is then read from Facebook's public embed endpoint's <c>hd_src</c>/
/// <c>sd_src</c> fields — no cipher/obfuscation involved, unlike YouTube. The fixtures are trimmed extracts
/// of real Facebook pages captured during TASK-101's research (see the fixture files); the <c>hd_src</c>
/// URL shape was confirmed empirically to be a real, directly downloadable MP4.
/// </summary>
public sealed class FacebookMediaExtractorTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static FacebookMediaExtractor Build(MapTransport transport) =>
        new(transport, NullLogger<FacebookMediaExtractor>.Instance);

    private static MediaRequest Request(string url) => new() { Url = new Uri(url) };

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixturesDir, name));

    [Fact]
    public async Task TryExtractAsync_NonFacebookUrl_ReturnsNull_WithoutFetching()
    {
        var transport = new MapTransport();

        MediaSource? source = await Build(transport).TryExtractAsync(Request("https://example.com/video.mp4"));

        source.Should().BeNull();
        transport.RequestedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_VideoUrlWithId_FetchesEmbedDirectly_ReturnsProgressiveSource()
    {
        const string embedUrl = "https://www.facebook.com/video/embed?video_id=10153231379946729";
        var transport = new MapTransport().AddText(embedUrl, ReadFixture("facebook-embed-real.html"));

        MediaSource? source = await Build(transport).TryExtractAsync(
            Request("https://www.facebook.com/facebook/videos/10153231379946729/"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("facebook");
        source.Kind.Should().Be(MediaKind.Progressive);
        source.Url.ToString().Should().StartWith("https://video.fkhi20-1.fna.fbcdn.net/").And.Contain(".mp4");
        source.Url.ToString().Should().NotContain(@"\/", "the JSON \\/ escapes must be unescaped");
        source.SuggestedFileName.Should().Be("facebook-10153231379946729");

        // The id was already in the URL, so only the embed endpoint should have been hit — no page fetch.
        transport.RequestedUrls.Should().ContainSingle().Which.Should().Be(embedUrl);
    }

    [Fact]
    public async Task TryExtractAsync_OpaqueShortLink_ResolvesIdFromPageBody_ThenFetchesEmbed()
    {
        const string shortLink = "https://fb.watch/abc123XYZ/";
        const string embedUrl = "https://www.facebook.com/video/embed?video_id=10153231379946729";
        var transport = new MapTransport()
            .AddText(shortLink, ReadFixture("facebook-watch-page-no-embed-data.html"))
            .AddText(embedUrl, ReadFixture("facebook-embed-real.html"));

        MediaSource? source = await Build(transport).TryExtractAsync(Request(shortLink));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Progressive);
        transport.RequestedUrls.Should().Contain([shortLink, embedUrl]);
    }

    [Fact]
    public async Task TryExtractAsync_EmbedHasNoSrc_ReturnsNull()
    {
        const string embedUrl = "https://www.facebook.com/video/embed?video_id=99999999999999999";
        var transport = new MapTransport().AddText(
            embedUrl, "<html><body>This content isn't available right now.</body></html>");

        MediaSource? source = await Build(transport).TryExtractAsync(
            Request("https://www.facebook.com/someone/videos/99999999999999999/"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_ShortLinkWithNoResolvableId_ReturnsNull_WithoutFetchingEmbed()
    {
        const string shortLink = "https://fb.watch/nothingHere/";
        var transport = new MapTransport().AddText(shortLink, "<html><body>generic page, no video_id</body></html>");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(shortLink));

        source.Should().BeNull();
        transport.RequestedUrls.Should().ContainSingle().Which.Should().Be(shortLink);
    }

    [Fact]
    public async Task TryExtractAsync_UnfetchablePage_ReturnsNull()
    {
        MediaSource? source = await Build(new MapTransport())
            .TryExtractAsync(Request("https://fb.watch/missing/"));

        source.Should().BeNull();
    }
}
