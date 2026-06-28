using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace JustDownload.Tests.Media;

/// <summary>
/// Extractor evaluation corpus (TASK-043, PRD §3.3): a checked-in set of self-hosted, permissively-licensed
/// fixtures (HLS plain + AES-128, progressive video + audio) that the generic extractors must handle. The
/// corpus must pass 100% on the self-hosted set (AC1); it is checked in as <c>Fixtures/extractor-corpus.json</c>
/// (AC0) and runs nightly in CI (AC2, see <c>.github/workflows/nightly-corpus.yml</c>). It contains only
/// self-hosted cases — flaky third-party sites belong in a separate, quarantined trait, not this gate.
/// </summary>
[Trait("Category", "ExtractorCorpus")]
public sealed class ExtractorCorpusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-corpus-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ExtractorCorpusTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    private static readonly JsonSerializerOptions CorpusJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record CorpusEntry(
        string Name, string Type, bool Encrypted, bool ViaMaster, int Segments, string? Ext);

    private static List<CorpusEntry> LoadCorpus()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "extractor-corpus.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<CorpusEntry>>(stream, CorpusJsonOptions) ?? [];
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();
        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Corpus_IsCheckedIn_WithEnoughCases()
    {
        List<CorpusEntry> corpus = LoadCorpus();
        corpus.Should().HaveCountGreaterThanOrEqualTo(30, "~30 self-hosted fixtures are checked in (AC0)");
        corpus.Select(c => c.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AllSelfHostedFixtures_Pass()
    {
        List<CorpusEntry> corpus = LoadCorpus();
        using ServiceProvider provider = BuildProvider();
        var registry = provider.GetRequiredService<IMediaExtractorRegistry>();
        var downloader = provider.GetRequiredService<IHlsDownloader>();
        var concat = provider.GetRequiredService<IHlsConcatenator>();

        var failures = new List<string>();
        foreach (CorpusEntry entry in corpus)
        {
            try
            {
                if (entry.Type == "progressive")
                {
                    await EvaluateProgressiveAsync(registry, entry);
                }
                else
                {
                    await EvaluateHlsAsync(registry, downloader, concat, entry);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {ex.Message}");
            }
        }

        _output.WriteLine($"corpus: {corpus.Count - failures.Count}/{corpus.Count} passed");
        failures.Should().BeEmpty("the generic extractors must succeed on 100% of the self-hosted corpus (AC1)");
    }

    private static async Task EvaluateProgressiveAsync(IMediaExtractorRegistry registry, CorpusEntry entry)
    {
        var url = new Uri($"https://cdn.example.com/{entry.Name}{entry.Ext}");
        MediaSource? source = await registry.ExtractAsync(new MediaRequest { Url = url });
        if (source is null || source.Kind != MediaKind.Progressive)
        {
            throw new InvalidOperationException($"expected Progressive, got {source?.Kind.ToString() ?? "null"}");
        }
    }

    private async Task EvaluateHlsAsync(
        IMediaExtractorRegistry registry, IHlsDownloader downloader, IHlsConcatenator concat, CorpusEntry entry)
    {
        var segments = new List<byte[]>(entry.Segments);
        for (int i = 0; i < entry.Segments; i++)
        {
            segments.Add(RandomNumberGenerator.GetBytes(2048));
        }

        await using var server = new LoopbackHlsServer(segments, entry.Encrypted);
        Uri sourceUrl = entry.ViaMaster ? server.MasterUrl : server.MediaUrl;

        MediaSource? source = await registry.ExtractAsync(new MediaRequest { Url = sourceUrl });
        if (source is null || source.Kind != MediaKind.Hls)
        {
            throw new InvalidOperationException($"expected Hls, got {source?.Kind.ToString() ?? "null"}");
        }

        Uri mediaUri = entry.ViaMaster ? new Uri(source.Variants[0].Id) : sourceUrl;
        string work = Path.Combine(_dir, entry.Name);
        HlsDownloadResult result = await downloader.DownloadAsync(mediaUri, work);
        if (result.SegmentFiles.Count != entry.Segments)
        {
            throw new InvalidOperationException($"got {result.SegmentFiles.Count} segments, expected {entry.Segments}");
        }

        string output = Path.Combine(_dir, entry.Name + ".ts");
        await concat.ConcatenateAsync(result.SegmentFiles, output);

        string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output)));
        string expected = Convert.ToHexString(SHA256.HashData(server.ReferenceBytes));
        if (actual != expected)
        {
            throw new InvalidOperationException("reassembled bytes did not match the reference (SHA-256 mismatch)");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
