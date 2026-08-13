using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Transport;
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
    public async Task DownloadAsync_ExtXMap_PrependsInitializationSegment()
    {
        // Regression: Twitter/X (and every CMAF stream) puts the ftyp/moov boxes in the #EXT-X-MAP
        // initialization segment. Downloading only the .m4s fragments produced a file players rejected with
        // "trun track id unknown, no tfhd was found", so the init segment must lead the concat input.
        const string playlistUrl = "https://cdn/f/media.m3u8";
        byte[] init = Encoding.ASCII.GetBytes("FTYP-MOOV-INIT");
        byte[] f0 = Encoding.ASCII.GetBytes("FRAGMENT-ZERO");
        byte[] f1 = Encoding.ASCII.GetBytes("FRAGMENT-ONE");

        var transport = new MapTransport()
            .AddText(playlistUrl,
                "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:3,\nf0.m4s\n#EXTINF:3,\nf1.m4s\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/f/init.mp4", init)
            .AddBytes("https://cdn/f/f0.m4s", f0)
            .AddBytes("https://cdn/f/f1.m4s", f1);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        result.SegmentFiles.Should().HaveCount(3, "the init segment leads the two media fragments");
        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(init);
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(f0);
        (await File.ReadAllBytesAsync(result.SegmentFiles[2])).Should().Equal(f1);
        result.TotalBytes.Should().Be(init.Length + f0.Length + f1.Length);
    }

    [Fact]
    public async Task DownloadAsync_ByteRangeSegments_RequestsRanges_AndWritesExactSubRanges()
    {
        // One resource, sub-ranged per segment (#EXT-X-BYTERANGE) — the shape fMP4 CDNs serve. The second
        // fragment omits its offset and continues after the first.
        const string playlistUrl = "https://cdn/b/media.m3u8";
        byte[] resource = Encoding.ASCII.GetBytes("INITINITf0f0f0f0f0f1f1f1f1TRAILING");
        byte[] init = resource[..8];
        byte[] f0 = resource[8..18];
        byte[] f1 = resource[18..26];

        var transport = new MapTransport()
            .AddText(playlistUrl,
                "#EXTM3U\n#EXT-X-MAP:URI=\"all.mp4\",BYTERANGE=\"8@0\"\n" +
                "#EXT-X-BYTERANGE:10@8\n#EXTINF:3,\nall.mp4\n" +
                "#EXT-X-BYTERANGE:8\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/b/all.mp4", resource);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        result.SegmentFiles.Should().HaveCount(3, "the init sub-range leads the two fragment sub-ranges");
        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(init);
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(f0);
        (await File.ReadAllBytesAsync(result.SegmentFiles[2])).Should().Equal(f1);
        result.TotalBytes.Should().Be(init.Length + f0.Length + f1.Length);

        transport.RequestedRanges.Should().BeEquivalentTo(new[]
        {
            new ByteRange(0, 7),
            new ByteRange(8, 17),
            new ByteRange(18, 25),
        });
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoresRangeHeader_SlicesLocally_RatherThanAppendingWholeResource()
    {
        // Adversarial server: answers a ranged request with 200 OK and the entire resource. Appending that
        // whole body per segment would triple the output and corrupt it (CLAUDE.md §5, no silent failures).
        const string playlistUrl = "https://cdn/r/media.m3u8";
        byte[] resource = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJ");

        var transport = new MapTransport { IgnoreRangeRequests = true }
            .AddText(playlistUrl,
                "#EXTM3U\n#EXT-X-BYTERANGE:5@0\n#EXTINF:3,\nall.mp4\n" +
                "#EXT-X-BYTERANGE:5\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/r/all.mp4", resource);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        (await File.ReadAllBytesAsync(result.SegmentFiles[0])).Should().Equal(Encoding.ASCII.GetBytes("01234"));
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(Encoding.ASCII.GetBytes("56789"));
        result.TotalBytes.Should().Be(10, "only the requested sub-ranges count, not the full body twice");
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoresRange_AndBodyCannotContainTheSubRange_Throws()
    {
        const string playlistUrl = "https://cdn/t/media.m3u8";

        var transport = new MapTransport { IgnoreRangeRequests = true }
            .AddText(playlistUrl,
                "#EXTM3U\n#EXT-X-BYTERANGE:1000@800\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/t/all.mp4", Encoding.ASCII.GetBytes("far too short"));

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        await act.Should().ThrowAsync<HlsExtractionException>().WithMessage("*all.mp4*ignored the Range*");
    }

    [Fact]
    public async Task DownloadAsync_PlaylistWithoutByteRanges_SendsNoRangeHeader()
    {
        // Regression guard: the common unranged playlist must behave exactly as before byte-range support.
        const string playlistUrl = "https://cdn/u/media.m3u8";
        byte[] s0 = Encoding.ASCII.GetBytes("PLAIN-ZERO");

        var transport = new MapTransport()
            .AddText(playlistUrl, "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/u/init.mp4", Encoding.ASCII.GetBytes("INIT"))
            .AddBytes("https://cdn/u/seg0.ts", s0);

        HlsDownloadResult result = await Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        transport.RequestedRanges.Should().BeEmpty();
        (await File.ReadAllBytesAsync(result.SegmentFiles[1])).Should().Equal(s0);
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
    public async Task DownloadAsync_UnreachableInitializationSegment_Throws_RatherThanEmittingFragmentsAlone()
    {
        // The failure mode this must never regress into: silently skipping an unfetchable init segment and
        // returning fragments that concatenate into an unplayable file (CLAUDE.md §5, no silent failures).
        const string playlistUrl = "https://cdn/g/media.m3u8";
        var transport = new MapTransport()
            .AddText(playlistUrl, "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:3,\nf0.m4s\n#EXT-X-ENDLIST\n")
            .AddBytes("https://cdn/g/f0.m4s", Encoding.ASCII.GetBytes("FRAGMENT"));

        Func<Task> act = () => Build(transport).DownloadAsync(new Uri(playlistUrl), _workDir);

        await act.Should().ThrowAsync<HlsExtractionException>().WithMessage("*init.mp4*");
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
