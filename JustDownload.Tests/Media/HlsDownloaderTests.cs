using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JustDownload.Core.Media.Hls;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// HLS segment downloading (TASK-037): parallel fetch (AC1), AES-128 decryption with explicit and
/// sequence-derived IVs (AC2), segment-count progress (AC3), in-order output, and the failure contracts.
/// Runs entirely against an in-memory <see cref="MapTransport"/>.
/// </summary>
public sealed class HlsDownloaderTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "jd-hls-" + Guid.NewGuid().ToString("N"));

    private static HlsDownloader Build(MapTransport transport, HlsOptions? options = null) =>
        new(transport, options ?? new HlsOptions(), NullLogger<HlsDownloader>.Instance);

    [Fact]
    public async Task DownloadAsync_PlainSegments_DownloadsInOrder_AndReportsSegmentProgress()
    {
        const string playlistUrl = "https://cdn/x/media.m3u8";
        byte[] s0 = Encoding.ASCII.GetBytes("SEGMENT-ZERO");
        byte[] s1 = Encoding.ASCII.GetBytes("SEGMENT-ONE-LONGER");
        byte[] s2 = Encoding.ASCII.GetBytes("S2");

        var transport = new MapTransport()
            .AddText(playlistUrl,
                "#EXTM3U\n#EXTINF:6,\nseg0.ts\n#EXTINF:6,\nseg1.ts\n#EXTINF:6,\nseg2.ts\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/x/seg0.ts", s0)
            .AddBytes("https://cdn/x/seg1.ts", s1)
            .AddBytes("https://cdn/x/seg2.ts", s2);

        var progress = new List<HlsProgress>();
        HlsDownloadResult result = await Build(transport).DownloadAsync(
            new Uri(playlistUrl), _workDir,
            progress: new Progress<HlsProgress>(p => { lock (progress) { progress.Add(p); } }));

        result.SegmentFiles.Should().HaveCount(3);
        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(s0);
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(s1);
        (await File.ReadAllBytesAsync(result.SegmentFiles[2])).Should().Equal(s2);
        result.TotalBytes.Should().Be(s0.Length + s1.Length + s2.Length);

        await Task.Delay(50);
        lock (progress)
        {
            progress.Should().NotBeEmpty();
            progress.Max(p => p.CompletedSegments).Should().Be(3);
            progress.Should().OnlyContain(p => p.TotalSegments == 3);
        }
    }

    [Fact]
    public async Task DownloadAsync_Aes128_ExplicitIv_DecryptsToPlaintext()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] plain = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog, twice over!!");
        byte[] cipher = EncryptAes128(plain, key, iv);
        string ivHex = "0x" + Convert.ToHexString(iv);

        const string playlistUrl = "https://cdn/e/media.m3u8";
        var transport = new MapTransport()
            .AddText(playlistUrl,
                $"#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV={ivHex}\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/e/key.bin", key)
            .AddBytes("https://cdn/e/seg0.ts", cipher);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(plain);
    }

    [Fact]
    public async Task DownloadAsync_Aes128_SequenceDerivedIv_DecryptsToPlaintext()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        const long sequence = 7;
        byte[] iv = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), sequence);
        byte[] plain = Encoding.ASCII.GetBytes("sequence-derived IV path exercised here");
        byte[] cipher = EncryptAes128(plain, key, iv);

        const string playlistUrl = "https://cdn/d/media.m3u8";
        var transport = new MapTransport()
            .AddText(playlistUrl,
                "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n" +
                "#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/d/key.bin", key)
            .AddBytes("https://cdn/d/seg0.ts", cipher);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(plain);
    }

    [Fact]
    public async Task DownloadAsync_FetchesSegmentsInParallel()
    {
        var sb = new StringBuilder("#EXTM3U\n");
        var transport = new MapTransport { ResponseDelay = TimeSpan.FromMilliseconds(60) };
        const string playlistUrl = "https://cdn/p/media.m3u8";
        for (int i = 0; i < 8; i++)
        {
            sb.Append("#EXTINF:6,\n").Append(System.Globalization.CultureInfo.InvariantCulture, $"seg{i}.ts\n");
            transport.AddBytes($"https://cdn/p/seg{i}.ts", Encoding.ASCII.GetBytes($"seg-{i}"));
        }

        sb.Append("#EXT-X-ENDLIST\n");
        transport.AddText(playlistUrl, sb.ToString());

        await Build(transport, new HlsOptions { MaxParallelSegments = 4 })
            .DownloadAsync(new Uri(playlistUrl), _workDir);

        transport.PeakConcurrency.Should().BeGreaterThan(1, "segments are fetched concurrently");
        transport.PeakConcurrency.Should().BeLessThanOrEqualTo(4, "concurrency is bounded by the option");
    }

    [Fact]
    public async Task DownloadAsync_MasterPlaylist_Throws()
    {
        const string playlistUrl = "https://cdn/m/master.m3u8";
        var transport = new MapTransport().AddText(playlistUrl,
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow.m3u8\n");

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        await act.Should().ThrowAsync<HlsExtractionException>().WithMessage("*master playlist*");
    }

    [Fact]
    public async Task DownloadAsync_EmptyPlaylist_Throws()
    {
        const string playlistUrl = "https://cdn/n/media.m3u8";
        var transport = new MapTransport().AddText(playlistUrl, "#EXTM3U\n#EXT-X-ENDLIST\n");

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        await act.Should().ThrowAsync<HlsExtractionException>().WithMessage("*no segments*");
    }

    [Fact]
    public async Task DownloadAsync_SampleAes_Throws()
    {
        const string playlistUrl = "https://cdn/s/media.m3u8";
        var transport = new MapTransport().AddText(playlistUrl,
            "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n");

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        await act.Should().ThrowAsync<HlsExtractionException>().WithMessage("*SAMPLE-AES*");
    }

    private static byte[] EncryptAes128(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }
    }
}
