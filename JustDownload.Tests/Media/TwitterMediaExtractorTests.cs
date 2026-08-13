using FluentAssertions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Twitter;
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

    private static TwitterMediaExtractor Build(MapTransport transport) =>
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
        source.Variants.Should().BeEmpty("the existing HLS extractor parses the master — heights are not fabricated here");
        source.SuggestedFileName.Should().Be("Check out this video!");
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
