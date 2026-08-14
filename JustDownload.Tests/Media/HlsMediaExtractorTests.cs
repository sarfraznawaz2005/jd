using FluentAssertions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The HLS extractor's detection and variant listing (TASK-037 AC0): a master playlist's variants are
/// surfaced for quality selection, a media playlist yields a single HLS source, and non-HLS or unfetchable
/// URLs decline (return null) so the registry degrades gracefully.
/// </summary>
public sealed class HlsMediaExtractorTests
{
    private static HlsMediaExtractor Build(MapTransport transport) =>
        new(transport, NullLogger<HlsMediaExtractor>.Instance);

    private static MediaRequest Request(string url, string? contentType = null) =>
        new() { Url = new Uri(url), ContentType = contentType };

    [Fact]
    public async Task TryExtractAsync_MasterPlaylist_ListsVariants()
    {
        const string url = "https://cdn/v/master.m3u8";
        var transport = new MapTransport().AddText(url,
            "#EXTM3U\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n360.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\n1080.m3u8\n");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().HaveCount(2);
        source.Variants.Select(v => v.Height).Should().Contain(360).And.Contain(1080);
        source.Variants.Should().Contain(v => v.Id == "https://cdn/v/1080.m3u8");
    }

    [Fact]
    public async Task TryExtractAsync_MasterPlaylistWithAlternateAudio_PopulatesAudioVariants()
    {
        // The generic-extractor half of the alternate-audio fix: any plain .m3u8 master (not just Twitter's)
        // that declares a separate #EXT-X-MEDIA:TYPE=AUDIO rendition with a URI must surface it on
        // MediaSource.AudioVariants so the picker can offer it and the coordinator can fetch + mux it.
        const string url = "https://cdn/v/master.m3u8";
        var transport = new MapTransport().AddText(url,
            "#EXTM3U\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"audio/index.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2176000,RESOLUTION=1280x720,CODECS=\"avc1.640020\",AUDIO=\"aud\"\n720p.m3u8\n");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Variants.Should().HaveCount(1);
        source.AudioVariants.Should().HaveCount(1);
        source.AudioVariants[0].Id.Should().Be("https://cdn/v/audio/index.m3u8");
        source.AudioVariants[0].Language.Should().Be("en");
    }

    [Fact]
    public async Task TryExtractAsync_MasterPlaylistWithMuxedAudio_AudioVariantsEmpty()
    {
        // Regression guard: variants whose audio is muxed into the segments (no #EXT-X-MEDIA:TYPE=AUDIO
        // group at all) must keep AudioVariants empty — there is nothing separate to select.
        const string url = "https://cdn/v/master.m3u8";
        var transport = new MapTransport().AddText(url,
            "#EXTM3U\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"mp4a.40.2,avc1.4d001f\"\n360.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080,CODECS=\"mp4a.40.2,avc1.640020\"\n1080.m3u8\n");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Variants.Should().HaveCount(2);
        source.AudioVariants.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_MediaPlaylist_ReturnsSingleHlsSource_NoVariants()
    {
        const string url = "https://cdn/v/media.m3u8";
        var transport = new MapTransport().AddText(url,
            "#EXTM3U\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_DetectsByContentType_WhenExtensionMissing()
    {
        const string url = "https://cdn/stream?id=42";
        var transport = new MapTransport().AddText(url, "#EXTM3U\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n");

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request(url, contentType: "application/vnd.apple.mpegurl"));

        source!.Kind.Should().Be(MediaKind.Hls);
    }

    [Fact]
    public async Task TryExtractAsync_NonHlsUrl_ReturnsNull()
    {
        MediaSource? source = await Build(new MapTransport()).TryExtractAsync(Request("https://x/clip.mp4"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_UnfetchablePlaylist_ReturnsNull()
    {
        // .m3u8 extension but the transport has no body mapped → 404 → decline gracefully.
        MediaSource? source = await Build(new MapTransport()).TryExtractAsync(Request("https://x/missing.m3u8"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_M3u8UrlButNotAPlaylist_ReturnsNull()
    {
        const string url = "https://x/fake.m3u8";
        var transport = new MapTransport().AddText(url, "<html>not a playlist</html>");

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().BeNull();
    }
}
