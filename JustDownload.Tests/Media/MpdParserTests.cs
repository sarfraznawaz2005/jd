using FluentAssertions;
using JustDownload.Core.Media.Dash;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Pure DASH manifest parsing (TASK-039/102): classifying video/audio adaptation sets, resolving the
/// BaseURL chain to absolute file URLs for the progressive case, resolving SegmentTemplate/SegmentList
/// representations into a re-resolvable identifier at parse time and their ordered segment URIs at download
/// time (<see cref="MpdParser.ResolveSegments"/>), and skipping representations that stay genuinely
/// unresolvable (no BaseURL, and no timeline/duration to determine a segment count from).
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
    public void Parse_SegmentTemplateWithNoTimelineOrDuration_StaysUnresolvable_Skips()
    {
        // No SegmentTimeline, no @duration, no Period/MPD duration, no @endNumber — a segment count genuinely
        // cannot be determined (this is the shape of a dynamic/live MPD, out of scope for TASK-102).
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

        manifest.VideoRepresentations.Should().BeEmpty("no BaseURL and no way to compute a segment count");
        manifest.IsSegmented.Should().BeFalse();
    }

    [Fact]
    public void Parse_SegmentTemplateWithoutRepresentationId_Skips()
    {
        // A representation id is required to build a stable, re-resolvable identifier (and is mandatory per
        // the DASH schema anyway).
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="seg-$Number$.m4s" duration="4" timescale="1" />
                  <Representation bandwidth="2500000" width="1920" height="1080" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.VideoRepresentations.Should().BeEmpty("no representation id means no re-resolvable identifier");
    }

    [Fact]
    public void Parse_SegmentTemplate_NumberAddressing_WithMpdDuration_ResolvesSegmentCount()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="seg-$RepresentationID$-$Number%03d$.m4s" initialization="init-$RepresentationID$.mp4"
                                   duration="4" timescale="1" startNumber="1" />
                  <Representation id="v0" bandwidth="2500000" width="1920" height="1080" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.IsSegmented.Should().BeTrue();
        manifest.VideoRepresentations.Should().ContainSingle();
        DashRepresentation rep = manifest.VideoRepresentations[0];
        rep.Height.Should().Be(1080);

        MpdParser.TryParseRepresentationUri(rep.Uri, out Uri? manifestUri, out string? repId).Should().BeTrue();
        manifestUri.Should().Be(ManifestUri);
        repId.Should().Be("v0");

        IReadOnlyList<Uri>? segments = MpdParser.ResolveSegments(xml, manifestUri!, repId!);
        segments.Should().NotBeNull();
        segments.Should().Equal(
            new Uri("https://cdn.example.com/dash/init-v0.mp4"),
            new Uri("https://cdn.example.com/dash/seg-v0-001.m4s"),
            new Uri("https://cdn.example.com/dash/seg-v0-002.m4s"));
    }

    [Fact]
    public void Parse_SegmentTemplate_TimeAddressing_WithSegmentTimeline_ResolvesExactTimes()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="seg-$Time$.m4s" initialization="init.mp4" timescale="1">
                    <SegmentTimeline>
                      <S t="0" d="4" />
                      <S d="4" r="1" />
                      <S t="12" d="2" />
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v0" bandwidth="1000000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        MpdParser.TryParseRepresentationUri(manifest.VideoRepresentations[0].Uri, out Uri? manifestUri, out string? repId).Should().BeTrue();
        IReadOnlyList<Uri>? segments = MpdParser.ResolveSegments(xml, manifestUri!, repId!);

        segments.Should().Equal(
            new Uri("https://cdn.example.com/dash/init.mp4"),
            new Uri("https://cdn.example.com/dash/seg-0.m4s"),
            new Uri("https://cdn.example.com/dash/seg-4.m4s"),
            new Uri("https://cdn.example.com/dash/seg-8.m4s"),
            new Uri("https://cdn.example.com/dash/seg-12.m4s"));
    }

    [Fact]
    public void Parse_SegmentList_ExplicitSegmentUrls_ResolvesInOrder()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet contentType="audio">
                  <Representation id="a0" bandwidth="128000">
                    <SegmentList>
                      <Initialization sourceURL="init-a.m4a" />
                      <SegmentURL media="a-1.m4a" />
                      <SegmentURL media="a-2.m4a" />
                      <SegmentURL media="a-3.m4a" />
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        manifest.IsSegmented.Should().BeTrue();
        manifest.AudioRepresentations.Should().ContainSingle();

        MpdParser.TryParseRepresentationUri(manifest.AudioRepresentations[0].Uri, out Uri? manifestUri, out string? repId).Should().BeTrue();
        IReadOnlyList<Uri>? segments = MpdParser.ResolveSegments(xml, manifestUri!, repId!);

        segments.Should().Equal(
            new Uri("https://cdn.example.com/dash/init-a.m4a"),
            new Uri("https://cdn.example.com/dash/a-1.m4a"),
            new Uri("https://cdn.example.com/dash/a-2.m4a"),
            new Uri("https://cdn.example.com/dash/a-3.m4a"));
    }

    [Fact]
    public void Parse_RepresentationLevelSegmentTemplate_OverridesAdaptationSetLevel()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT2S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="set-level-$Number$.m4s" duration="1" timescale="1" />
                  <Representation id="v0" bandwidth="500000">
                    <SegmentTemplate media="rep-level-$Number$.m4s" duration="1" timescale="1" />
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest manifest = MpdParser.Parse(xml, ManifestUri);

        MpdParser.TryParseRepresentationUri(manifest.VideoRepresentations[0].Uri, out Uri? manifestUri, out string? repId).Should().BeTrue();
        IReadOnlyList<Uri>? segments = MpdParser.ResolveSegments(xml, manifestUri!, repId!);

        segments.Should().Equal(
            new Uri("https://cdn.example.com/dash/rep-level-1.m4s"),
            new Uri("https://cdn.example.com/dash/rep-level-2.m4s"));
    }

    [Fact]
    public void ResolveSegments_UnknownRepresentationId_ReturnsNull()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT4S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="seg-$Number$.m4s" duration="2" timescale="1" />
                  <Representation id="v0" bandwidth="500000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        MpdParser.ResolveSegments(xml, ManifestUri, "does-not-exist").Should().BeNull();
    }

    [Fact]
    public void TryParseRepresentationUri_NonDashUri_ReturnsFalse()
    {
        MpdParser.TryParseRepresentationUri(new Uri("https://cdn.example.com/video_1080.mp4"), out Uri? manifestUri, out string? repId)
            .Should().BeFalse();
        manifestUri.Should().BeNull();
        repId.Should().BeNull();
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
