using FluentAssertions;
using JustDownload.Core.Media.Dash;
using JustDownload.Core.Media.Extraction;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// The DASH extractor's discovery (TASK-039/102): a plain BaseURL .mpd is reported as
/// <see cref="MediaKind.SeparateStreams"/>, a SegmentTemplate/SegmentList .mpd as <see cref="MediaKind.Dash"/>,
/// both with the representations surfaced, while non-DASH or genuinely unresolvable manifests decline
/// gracefully.
/// </summary>
public sealed class DashMediaExtractorTests
{
    private const string ProgressiveMpd = """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
          <Period>
            <AdaptationSet contentType="video" mimeType="video/mp4">
              <Representation bandwidth="2500000" width="1920" height="1080"><BaseURL>v1080.mp4</BaseURL></Representation>
            </AdaptationSet>
            <AdaptationSet contentType="audio">
              <Representation bandwidth="128000"><BaseURL>a.m4a</BaseURL></Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    private static DashMediaExtractor Build(MapTransport transport) =>
        new(transport, NullLogger<DashMediaExtractor>.Instance);

    private static MediaRequest Request(string url, string? contentType = null) =>
        new() { Url = new Uri(url), ContentType = contentType };

    [Fact]
    public async Task TryExtractAsync_ProgressiveMpd_ReportsSeparateStreams_WithVariants()
    {
        const string url = "https://cdn/d/manifest.mpd";
        var transport = new MapTransport().AddText(url, ProgressiveMpd);

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.SeparateStreams);
        source.Variants.Should().ContainSingle().Which.Height.Should().Be(1080);
        source.AudioVariants.Should().ContainSingle().Which.Bandwidth.Should().Be(128000);
    }

    [Fact]
    public async Task TryExtractAsync_DetectsByContentType()
    {
        const string url = "https://cdn/d/stream?id=9";
        var transport = new MapTransport().AddText(url, ProgressiveMpd);

        MediaSource? source = await Build(transport)
            .TryExtractAsync(Request(url, contentType: "application/dash+xml"));

        source!.Kind.Should().Be(MediaKind.SeparateStreams);
    }

    [Fact]
    public async Task TryExtractAsync_NonDashUrl_ReturnsNull()
    {
        MediaSource? source = await Build(new MapTransport()).TryExtractAsync(Request("https://x/clip.mp4"));

        source.Should().BeNull();
    }

    [Fact]
    public async Task TryExtractAsync_SegmentTemplateWithNoResolvableCount_ReturnsNull()
    {
        // No SegmentTimeline, no @duration, no Period/MPD duration — a dynamic/live shape, out of scope
        // (TASK-102) — so no segment count can be determined and the manifest still declines gracefully.
        const string url = "https://cdn/d/live.mpd";
        const string templateMpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period><AdaptationSet contentType="video">
                <SegmentTemplate media="seg-$Number$.m4s" />
                <Representation id="v0" bandwidth="2500000" width="1920" height="1080" />
              </AdaptationSet></Period>
            </MPD>
            """;
        var transport = new MapTransport().AddText(url, templateMpd);

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().BeNull("no downloadable representation — degrade gracefully");
    }

    [Fact]
    public async Task TryExtractAsync_SegmentTemplateMpd_ReportsDashKind_WithVariants()
    {
        const string url = "https://cdn/d/vod.mpd";
        const string templateMpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate media="v-$Number$.m4s" initialization="v-init.mp4" duration="4" timescale="1" />
                  <Representation id="v0" bandwidth="2500000" width="1920" height="1080" />
                </AdaptationSet>
                <AdaptationSet contentType="audio">
                  <SegmentTemplate media="a-$Number$.m4s" initialization="a-init.mp4" duration="4" timescale="1" />
                  <Representation id="a0" bandwidth="128000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var transport = new MapTransport().AddText(url, templateMpd);

        MediaSource? source = await Build(transport).TryExtractAsync(Request(url));

        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Dash);
        source.Variants.Should().ContainSingle().Which.Height.Should().Be(1080);
        source.AudioVariants.Should().ContainSingle().Which.Bandwidth.Should().Be(128000);
        source.Variants[0].Id.Should().Contain(url).And.Contain("dash-rep=v0");
    }

    [Fact]
    public async Task TryExtractAsync_UnfetchableManifest_ReturnsNull()
    {
        MediaSource? source = await Build(new MapTransport()).TryExtractAsync(Request("https://x/missing.mpd"));

        source.Should().BeNull();
    }
}
