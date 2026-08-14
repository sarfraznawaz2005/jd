using FluentAssertions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Twitter;
using JustDownload.Core.Transport;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The Twitter/X extractor (D3, in-house): a <c>x.com</c>/<c>twitter.com</c> <c>/status/&lt;id&gt;</c> URL
/// is recognised and the public syndication API (<c>cdn.syndication.twimg.com/tweet-result</c>) is queried
/// with the deterministic token — no auth, no cookies, no bot-detection bypass. The fixtures below are
/// trimmed syndication JSON shapes (documented in the task's research findings); the <see cref="MapTransport"/>
/// serves a canned body for the syndication host so no live network is needed. The token formula itself is
/// cross-checked against Node's native <c>Number.prototype.toString(36)</c> for several known tweet ids.
/// </summary>
public sealed class TwitterMediaExtractorTests
{
    private const string TweetId = "1736692012007956480";
    private const string MasterUrl = "https://video.twimg.com/ext_tw_video/abc/master.m3u8";
    private const string Mp4Low = "https://video.twimg.com/ext_tw_video/abc/low.mp4";
    private const string Mp4High = "https://video.twimg.com/ext_tw_video/abc/high.mp4";

    private static readonly string SyndicationUrl =
        $"https://cdn.syndication.twimg.com/tweet-result?id={TweetId}&token={TwitterSyndicationToken.Generate(TweetId)}&lang=en";

    private static TwitterMediaExtractor Build(ITransport transport) =>
        new(transport, NullLogger<TwitterMediaExtractor>.Instance);

    private static MediaRequest Request(string url) => new() { Url = new Uri(url) };

    private static string HlsFixture(string? text = "Check out this video!") =>
        $$"""
        {
          "text": "{{text}}",
          "__typename": "Tweet",
          "mediaDetails": [
            {
              "type": "video",
              "video_info": {
                "variants": [
                  { "content_type": "application/x-mpegURL", "url": "{{MasterUrl}}" },
                  { "content_type": "video/mp4", "bitrate": 832000, "url": "{{Mp4Low}}" },
                  { "content_type": "video/mp4", "bitrate": 2176000, "url": "{{Mp4High}}" }
                ]
              }
            }
          ]
        }
        """;

    private static string MasterPlaylistFixture() =>
        """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=832000,RESOLUTION=640x360
        360p/playlist.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2176000,RESOLUTION=1280x720
        720p/playlist.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080
        1080p/playlist.m3u8
        """;

    private static string MediaPlaylistFixture() =>
        """
        #EXTM3U
        #EXT-X-TARGETDURATION:10
        #EXTINF:9.009,
        segment0.ts
        #EXT-X-ENDLIST
        """;

    /// <summary>
    /// Shaped like a real video.twimg.com master playlist (verified against captured Twitter/X CDN output
    /// during this task's research): <c>#EXT-X-STREAM-INF</c> variants whose <c>CODECS</c> lists both
    /// <c>mp4a.40.2</c> (audio) and <c>avc1...</c> (video) with no <c>#EXT-X-MEDIA:TYPE=AUDIO</c> group at
    /// all — audio is muxed into each variant's own fMP4 segments, not a separate rendition.
    /// </summary>
    private static string MasterPlaylistFixtureNoAlternateAudio() =>
        """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=205381,BANDWIDTH=264621,RESOLUTION=640x360,CODECS="mp4a.40.2,avc1.4d001f"
        pl/640x360/emjUVd3t94tT8j-1.m3u8?container=fmp4
        #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=501854,BANDWIDTH=639498,RESOLUTION=1280x720,CODECS="mp4a.40.2,avc1.640020"
        pl/1280x720/_u3fHmjJPHlXgv3W.m3u8?container=fmp4
        """;

    /// <summary>A master playlist with a genuine separate audio rendition (RFC 8216 §4.3.4.1).</summary>
    private static string MasterPlaylistFixtureWithAlternateAudio() =>
        """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/index.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=2176000,RESOLUTION=1280x720,CODECS="avc1.640020",AUDIO="aud"
        720p/playlist.m3u8
        """;

    /// <summary>Throws for one specific URL (simulating a network failure) and delegates every other request.</summary>
    private sealed class ThrowingForUrlTransport(ITransport inner, string throwForUrl) : ITransport
    {
        public Task<ITransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken = default) =>
            string.Equals(request.Uri.ToString(), throwForUrl, StringComparison.Ordinal)
                ? throw new HttpRequestException("Simulated network failure.")
                : inner.SendAsync(request, cancellationToken);
    }

    private static string Mp4OnlyFixture() =>
        $$"""
        {
          "text": "just an mp4",
          "__typename": "Tweet",
          "video": {
            "aspectRatio": [16, 9],
            "durationMs": 12345,
            "variants": [
              { "type": "video/mp4", "bitrate": 832000, "src": "{{Mp4Low}}" },
              { "type": "video/mp4", "bitrate": 2176000, "src": "{{Mp4High}}" }
            ]
          }
        }
        """;

    [Fact]
    public async Task TryExtractAsync_XDotComStatusWithHlsMaster_ReturnsHlsMediaSource()
    {
        var transport = new MapTransport().AddText(SyndicationUrl, HlsFixture());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("twitter");
        source.Kind.Should().Be(MediaKind.Hls, "the HLS master is preferred over the MP4 variants");
        source.Url.ToString().Should().Be(MasterUrl);
        source.Variants.Should().BeEmpty("the master playlist itself was never stubbed on the transport, so the fetch 404s and degrades gracefully");
        source.SuggestedFileName.Should().Be("Check out this video!");
    }

    [Fact]
    public async Task TryExtractAsync_HlsMasterPlaylist_ParsesRealVariantQualities()
    {
        var transport = new MapTransport()
            .AddText(SyndicationUrl, HlsFixture())
            .AddText(MasterUrl, MasterPlaylistFixture());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Url.ToString().Should().Be(MasterUrl);
        source.Variants.Should().HaveCount(3, "the master playlist advertises three quality variants");
        source.Variants.Select(v => v.Height).Should().BeEquivalentTo([360, 720, 1080]);
        source.Variants.Select(v => v.Id).Should().BeEquivalentTo(
        [
            "https://video.twimg.com/ext_tw_video/abc/360p/playlist.m3u8",
            "https://video.twimg.com/ext_tw_video/abc/720p/playlist.m3u8",
            "https://video.twimg.com/ext_tw_video/abc/1080p/playlist.m3u8",
        ]);
    }

    [Fact]
    public async Task TryExtractAsync_RealTwitterShapedMaster_NoAlternateAudioGroup_AudioVariantsEmpty()
    {
        // Regression guard for the "no audio" bug report: a real Twitter master playlist mixes audio into
        // each variant's own CODECS/segments and declares no #EXT-X-MEDIA:TYPE=AUDIO group at all, so there
        // is nothing separate to select — AudioVariants staying empty here is correct, not a bug, and the
        // video variant URL itself is expected to already carry the muxed audio through the plain HLS path.
        var transport = new MapTransport()
            .AddText(SyndicationUrl, HlsFixture())
            .AddText(MasterUrl, MasterPlaylistFixtureNoAlternateAudio());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().HaveCount(2);
        source.AudioVariants.Should().BeEmpty(
            "audio is muxed into each variant's segments, not offered as a separate rendition");
    }

    [Fact]
    public async Task TryExtractAsync_HlsMasterWithAlternateAudio_PopulatesAudioVariants()
    {
        // The fix under test: when a Twitter (or any) HLS master *does* declare a separate
        // #EXT-X-MEDIA:TYPE=AUDIO rendition, it must surface on MediaSource.AudioVariants so the picker can
        // offer it and the download coordinator can fetch + mux it — this was always [] before the fix.
        var transport = new MapTransport()
            .AddText(SyndicationUrl, HlsFixture())
            .AddText(MasterUrl, MasterPlaylistFixtureWithAlternateAudio());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().HaveCount(1);
        source.AudioVariants.Should().HaveCount(1);
        source.AudioVariants[0].Id.Should().Be("https://video.twimg.com/ext_tw_video/abc/audio/index.m3u8");
        source.AudioVariants[0].Language.Should().Be("en");
    }

    [Fact]
    public async Task TryExtractAsync_HlsMediaPlaylist_ReturnsHlsWithNoVariants()
    {
        var transport = new MapTransport()
            .AddText(SyndicationUrl, HlsFixture())
            .AddText(MasterUrl, MediaPlaylistFixture());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Url.ToString().Should().Be(MasterUrl);
        source.Variants.Should().BeEmpty("a media playlist has a single quality — nothing to choose from");
    }

    [Fact]
    public async Task TryExtractAsync_HlsMasterFetchThrows_DegradesGracefully_NoException()
    {
        var inner = new MapTransport().AddText(SyndicationUrl, HlsFixture());
        var transport = new ThrowingForUrlTransport(inner, MasterUrl);

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull("a master-playlist fetch failure must not fail the whole extraction");
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Url.ToString().Should().Be(MasterUrl);
        source.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_TwitterDotComStatus_WithOnlyMp4Variants_ReturnsHighestBitrateProgressive()
    {
        var transport = new MapTransport().AddText(SyndicationUrl, Mp4OnlyFixture());

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://twitter.com/someuser/status/{TweetId}"));

        source.Should().NotBeNull();
        source!.ExtractorName.Should().Be("twitter");
        source.Kind.Should().Be(MediaKind.Progressive);
        source.Url.ToString().Should().Be(Mp4High, "the highest-bitrate MP4 wins");
        source.Variants.Should().BeEmpty("no heights are invented for the MP4 fallback");
        source.SuggestedFileName.Should().Be("just an mp4");
    }

    [Fact]
    public async Task TryExtractAsync_NonTwitterUrl_ReturnsNull_WithoutFetching()
    {
        var transport = new MapTransport();

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request("https://example.com/video.mp4"));

        source.Should().BeNull();
        transport.RequestedUrls.Should().BeEmpty("non-Twitter URLs are not claimed");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"__typename\":\"TweetTombstone\"}")]
    public async Task TryExtractAsync_EmptyOrTombstoneBody_ReturnsNull_SoYtDlpCanFallback(string body)
    {
        var transport = new MapTransport().AddText(SyndicationUrl, body);

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/u/status/{TweetId}"));

        source.Should().BeNull("unavailable/tombstone degrades gracefully — never fakes, never throws");
    }

    [Fact]
    public async Task TryExtractAsync_Http404FromTransport_ReturnsNull()
    {
        // No body registered for the syndication URL => MapTransport answers 404 (YtDlp-style asserts).
        var transport = new MapTransport();

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request($"https://x.com/u/status/{TweetId}"));

        source.Should().BeNull();
    }

    [Fact]
    public void SyndicationToken_IsDeterministic_NonEmpty_Alphanumeric_AndMatchesReference()
    {
        string a = TwitterSyndicationToken.Generate(TweetId);
        string b = TwitterSyndicationToken.Generate(TweetId);

        a.Should().Be(b, "the token is deterministic (no random component)");
        a.Should().NotBeNullOrEmpty();
        a.Should().MatchRegex("^[0-9a-z]+$", "the token is base-36 alphanumeric after stripping 0 and .");

        // Cross-checked against Node's native Number.prototype.toString(36) during task research:
        //   ((1736692012007956480/1e15)*Math.PI).toString(36).replace(/0|\./g,'') === "47jz8lzse59"
        // The net task-runtime has no browser, but the build machine runs Node, so assert the agreed value.
        a.Should().Be("47jz8lzse59",
            "matches the reference JS (V8) computation of the deterministic token formula for this tweet id");
    }
}
