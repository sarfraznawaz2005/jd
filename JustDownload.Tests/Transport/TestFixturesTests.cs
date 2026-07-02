using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// The reusable test fixtures (TASK-082, PRD §5.3): the range / no-range HTTP servers (AC0), and the
/// self-hosted HLS server — plain and AES-128 — driven end-to-end through the real HttpTransport + HLS
/// extractor/downloader/concatenator (AC2). Every fixture is in-process and torn down on dispose (AC3).
/// </summary>
public sealed class TestFixturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-fixtures-" + Guid.NewGuid().ToString("N"));

    public TestFixturesTests() => Directory.CreateDirectory(_dir);

    private static ServiceProvider BuildMediaProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJustDownloadTransport();

        // The yt-dlp fallback extractor (TASK-163) needs ISettingsService; substitute a no-DB fake with the
        // (default, off) video-capture toggle rather than pulling in the full SQLite-backed settings store.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);

        services.AddJustDownloadMedia();
        return services.BuildServiceProvider();
    }

    private static List<byte[]> Segments(int count, int size)
    {
        var list = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(RandomNumberGenerator.GetBytes(size));
        }

        return list;
    }

    // --- AC0: range + no-range HTTP fixtures ------------------------------------------------------

    [Fact]
    public async Task RangeServer_HonoursRanges_NoRangeServer_DoesNot()
    {
        byte[] body = RandomNumberGenerator.GetBytes(4096);

        await using var ranged = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var plain = new LoopbackHttpServer { Body = body, SupportRanges = false };

        using var http = new System.Net.Http.HttpClient();
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ranged.Url("f.bin"));
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using System.Net.Http.HttpResponseMessage r1 = await http.SendAsync(req);
        ((int)r1.StatusCode).Should().Be(206, "the range fixture serves 206 Partial Content");

        using var req2 = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, plain.Url("f.bin"));
        req2.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using System.Net.Http.HttpResponseMessage r2 = await http.SendAsync(req2);
        ((int)r2.StatusCode).Should().Be(200, "the no-range fixture ignores Range and serves the whole body");
    }

    // --- AC2: self-hosted HLS (plain + AES-128) --------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HlsFixture_DownloadsAndConcatenates_ToReferenceBytes(bool encrypted)
    {
        IReadOnlyList<byte[]> segments = Segments(count: 5, size: 4096);
        await using var server = new LoopbackHlsServer(segments, encrypted);
        using ServiceProvider provider = BuildMediaProvider();

        // Master → variant via the real extractor over real HTTP.
        var registry = provider.GetRequiredService<IMediaExtractorRegistry>();
        MediaSource? source = await registry.ExtractAsync(new MediaRequest { Url = server.MasterUrl });
        source.Should().NotBeNull();
        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().ContainSingle();

        // Download the variant's segments (decrypting AES-128 when encrypted) and concatenate.
        var downloader = provider.GetRequiredService<IHlsDownloader>();
        string work = Path.Combine(_dir, encrypted ? "enc" : "plain");
        HlsDownloadResult result = await downloader.DownloadAsync(new Uri(source.Variants[0].Id), work);
        result.SegmentFiles.Should().HaveCount(5);

        var concat = provider.GetRequiredService<IHlsConcatenator>();
        string output = Path.Combine(_dir, (encrypted ? "enc" : "plain") + ".ts");
        await concat.ConcatenateAsync(result.SegmentFiles, output);

        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output)))
            .Should().Be(Convert.ToHexString(SHA256.HashData(server.ReferenceBytes)),
                $"the {(encrypted ? "AES-128" : "plain")} HLS stream reassembles to the reference bytes");
    }

    [Fact]
    public async Task HlsFixture_MediaPlaylistDirectly_IsRecognised()
    {
        await using var server = new LoopbackHlsServer(Segments(3, 1024), encrypted: false);
        using ServiceProvider provider = BuildMediaProvider();

        MediaSource? source = await provider.GetRequiredService<IMediaExtractorRegistry>()
            .ExtractAsync(new MediaRequest { Url = server.MediaUrl });

        source!.Kind.Should().Be(MediaKind.Hls);
        source.Variants.Should().BeEmpty("a media playlist is a single quality");
    }

    // --- AC3: fixtures are torn down (no leaked listeners) ----------------------------------------

    [Fact]
    public async Task DisposingFixture_StopsTheListener()
    {
        var server = new LoopbackHttpServer { Body = [1, 2, 3] };
        int port = server.BaseUri.Port;
        await server.DisposeAsync();

        // After teardown nothing accepts on the port.
        using var client = new TcpClient();
        Func<Task> connect = () => client.ConnectAsync("127.0.0.1", port);
        await connect.Should().ThrowAsync<SocketException>("the disposed fixture no longer listens");
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
