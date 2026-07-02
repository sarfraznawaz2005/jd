using System.Text;
using FluentAssertions;
using JustDownload.Core.Media.Dash;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// DASH segment downloading (TASK-102): re-fetches and re-parses the manifest from the identifier URI,
/// downloads the resolved init + media segments in order with bounded parallelism, and the failure contracts
/// when the identifier, manifest, or representation are not resolvable. Runs entirely against an in-memory
/// <see cref="MapTransport"/> (the real end-to-end path, including HTTP + concat + mux, is covered by
/// <c>DashLoopbackTests</c>).
/// </summary>
public sealed class DashSegmentDownloaderTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "jd-dash-" + Guid.NewGuid().ToString("N"));

    private static DashSegmentDownloader Build(MapTransport transport, DashOptions? options = null) =>
        new(transport, options ?? new DashOptions(), NullLogger<DashSegmentDownloader>.Instance);

    private const string ManifestUrl = "https://cdn/d/manifest.mpd";

    private static string TemplateMpd() =>
        """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT6S">
          <Period>
            <AdaptationSet contentType="video">
              <SegmentTemplate media="v-$Number$.m4s" initialization="v-init.mp4" duration="2" timescale="1" />
              <Representation id="v0" bandwidth="500000" />
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public async Task DownloadAsync_ResolvesAndDownloadsSegments_InOrder()
    {
        byte[] init = Encoding.ASCII.GetBytes("INIT");
        byte[] s0 = Encoding.ASCII.GetBytes("SEG-ZERO");
        byte[] s1 = Encoding.ASCII.GetBytes("SEG-ONE-LONGER");
        byte[] s2 = Encoding.ASCII.GetBytes("S2");

        var transport = new MapTransport()
            .AddText(ManifestUrl, TemplateMpd())
            .AddBytes("https://cdn/d/v-init.mp4", init)
            .AddBytes("https://cdn/d/v-1.m4s", s0)
            .AddBytes("https://cdn/d/v-2.m4s", s1)
            .AddBytes("https://cdn/d/v-3.m4s", s2);

        Uri representationUri = new(ManifestUrl + "#dash-rep=" + Uri.EscapeDataString("v0"));
        var progress = new List<DashSegmentProgress>();

        DashSegmentDownloadResult result = await Build(transport).DownloadAsync(
            representationUri, _workDir,
            progress: new Progress<DashSegmentProgress>(p => { lock (progress) { progress.Add(p); } }));

        result.SegmentFiles.Should().HaveCount(4);
        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(init);
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(s0);
        (await File.ReadAllBytesAsync(result.SegmentFiles[2])).Should().Equal(s1);
        (await File.ReadAllBytesAsync(result.SegmentFiles[3])).Should().Equal(s2);
        result.TotalBytes.Should().Be(init.Length + s0.Length + s1.Length + s2.Length);

        await Task.Delay(50);
        lock (progress)
        {
            progress.Should().NotBeEmpty();
            progress.Max(p => p.CompletedSegments).Should().Be(4);
            progress.Should().OnlyContain(p => p.TotalSegments == 4);
        }
    }

    [Fact]
    public async Task DownloadAsync_FetchesSegmentsInParallel()
    {
        var sb = new StringBuilder(
            """<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT16S"><Period><AdaptationSet contentType="video"><SegmentTemplate media="v-$Number$.m4s" duration="2" timescale="1" /><Representation id="v0" bandwidth="1" /></AdaptationSet></Period></MPD>""");

        var transport = new MapTransport { ResponseDelay = TimeSpan.FromMilliseconds(60) };
        transport.AddText(ManifestUrl, sb.ToString());
        for (int i = 1; i <= 8; i++)
        {
            transport.AddBytes($"https://cdn/d/v-{i}.m4s", Encoding.ASCII.GetBytes($"seg-{i}"));
        }

        Uri representationUri = new(ManifestUrl + "#dash-rep=v0");
        await Build(transport, new DashOptions { MaxParallelSegments = 4 }).DownloadAsync(representationUri, _workDir);

        transport.PeakConcurrency.Should().BeGreaterThan(1, "segments are fetched concurrently");
        transport.PeakConcurrency.Should().BeLessThanOrEqualTo(5, "concurrency is bounded (manifest fetch + <=4 segments)");
    }

    [Fact]
    public async Task DownloadAsync_NotADashIdentifierUri_Throws()
    {
        var transport = new MapTransport();

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri("https://cdn/d/v.mp4"), _workDir);

        await act.Should().ThrowAsync<DashExtractionException>().WithMessage("*not a resolvable*");
    }

    [Fact]
    public async Task DownloadAsync_ManifestFetchFails_Throws()
    {
        var transport = new MapTransport(); // manifest URL not registered -> 404
        Uri representationUri = new(ManifestUrl + "#dash-rep=v0");

        Func<Task> act = () => Build(transport).DownloadAsync(representationUri, _workDir);

        await act.Should().ThrowAsync<DashExtractionException>().WithMessage("*fetch*");
    }

    [Fact]
    public async Task DownloadAsync_RepresentationIdNotInManifest_Throws()
    {
        var transport = new MapTransport().AddText(ManifestUrl, TemplateMpd());
        Uri representationUri = new(ManifestUrl + "#dash-rep=does-not-exist");

        Func<Task> act = () => Build(transport).DownloadAsync(representationUri, _workDir);

        await act.Should().ThrowAsync<DashExtractionException>().WithMessage("*no resolvable segments*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }
    }
}
