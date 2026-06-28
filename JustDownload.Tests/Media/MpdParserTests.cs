using FluentAssertions;
using JustDownload.Core.Media.Dash;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Pure DASH manifest parsing (TASK-039): classifying video/audio adaptation sets, resolving the
/// BaseURL chain to absolute file URLs for the progressive case, and skipping SegmentTemplate-only
/// representations that have no downloadable file.
/// </summary>
public sealed class MpdParserTests
{
    private static readonly Uri ManifestUri = new("https://cdn.example.com/dash/manifest.mpd");

    [Fact]
    public void Parse_SeparateVideoAndAudio_WithBaseUrls()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v0" bandwidth="2500000" width="1920" height="1080" codecs="avc1.640028">
                    <BaseURL>video_1080.mp4</BaseURL>
                  </Representation>
                  <Representation id="v1" bandwidth="800000" width="640" height="360">
                    <BaseURL>video_360.mp4</BaseURL>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" lang="en">
                  <Representation id="a0" bandwidth="128000">
                    <BaseURL>audio_en.m4a</BaseURL>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.VideoRepresentations.Should().HaveCount(2);
        manifest.VideoRepresentations[0].Uri.Should().Be(new Uri("https://cdn.example.com/dash/video_1080.mp4"));
        manifest.VideoRepresentations[0].Height.Should().Be(1080);
        manifest.VideoRepresentations[0].Bandwidth.Should().Be(2500000);

        manifest.AudioRepresentations.Should().ContainSingle();
        manifest.AudioRepresentations[0].Uri.Should().Be(new Uri("https://cdn.example.com/dash/audio_en.m4a"));
        manifest.AudioRepresentations[0].Language.Should().Be("en");
        manifest.AudioRepresentations[0].Bandwidth.Should().Be(128000);
    }

    [Fact]
    public void Parse_ResolvesMpdLevelBaseUrlChain()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <BaseURL>media/</BaseURL>
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <Representation bandwidth="1000000"><BaseURL>v.mp4</BaseURL></Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.VideoRepresentations[0].Uri
            .Should().Be(new Uri("https://cdn.example.com/dash/media/v.mp4"));
    }

    [Fact]
    public void Parse_SkipsSegmentTemplateOnlyRepresentations()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="seg-$Number$.m4s" initialization="init.mp4" />
                  <Representation id="v0" bandwidth="2500000" width="1920" height="1080" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.VideoRepresentations.Should().BeEmpty("no BaseURL means no progressive file to download");
    }

    [Fact]
    public void Parse_AbsoluteBaseUrl_IsKept()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet mimeType="audio/mp4">
                  <Representation bandwidth="96000">
                    <BaseURL>https://audio.cdn/track.m4a</BaseURL>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.AudioRepresentations[0].Uri.Should().Be(new Uri("https://audio.cdn/track.m4a"));
    }

    [Fact]
    public void Parse_NonMpdRoot_ReturnsEmpty()
    {
        DashManifest manifest = MpdParser.Parse("<html><body/></html>", ManifestUri);

        manifest.VideoRepresentations.Should().BeEmpty();
        manifest.AudioRepresentations.Should().BeEmpty();
    }
}
