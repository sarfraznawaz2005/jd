using FluentAssertions;
using JustDownload.Core.Media.Hls;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Pure HLS playlist parsing (TASK-037 AC0/AC2): master variant listing, media segment ordering, relative
/// URI resolution, AES-128 key parsing, IV hex decoding, and the media-sequence numbering used to derive an
/// IV. All deterministic, no I/O.
/// </summary>
public sealed class M3U8ParserTests
{
    private static readonly Uri MasterUri = new("https://cdn.example.com/video/master.m3u8");

    [Fact]
    public void IsMaster_DistinguishesMasterFromMedia()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow.m3u8\n";
        const string media = "#EXTM3U\n#EXTINF:9.0,\nseg0.ts\n#EXT-X-ENDLIST\n";

        M3U8Parser.IsMaster(master).Should().BeTrue();
        M3U8Parser.IsMaster(media).Should().BeFalse();
    }

    [Fact]
    public void ParseMaster_ListsVariants_WithResolutionBandwidthAndResolvedUri()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\"\n" +
            "360/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2560000,RESOLUTION=1280x720\n" +
            "https://other.cdn/720/index.m3u8\n";

        HlsMasterPlaylist master = M3U8Parser.ParseMaster(content, MasterUri);

        master.Variants.Should().HaveCount(2);
        HlsVariant low = master.Variants[0];
        low.Bandwidth.Should().Be(1280000);
        low.Height.Should().Be(360);
        low.Width.Should().Be(640);
        low.Codecs.Should().Be("avc1.4d401e,mp4a.40.2");
        low.Uri.Should().Be(new Uri("https://cdn.example.com/video/360/index.m3u8"));

        // An absolute variant URI is kept as-is.
        master.Variants[1].Uri.Should().Be(new Uri("https://other.cdn/720/index.m3u8"));
        master.Variants[1].Height.Should().Be(720);
    }

    [Fact]
    public void ParseMedia_ReturnsSegmentsInOrder_WithResolvedUrisAndDurations()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-TARGETDURATION:10\n" +
            "#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXTINF:9.009,\n" +
            "seg0.ts\n" +
            "#EXTINF:9.009,\n" +
            "seg1.ts\n" +
            "#EXTINF:3.003,\n" +
            "seg2.ts\n" +
            "#EXT-X-ENDLIST\n";
        var mediaUri = new Uri("https://cdn.example.com/video/360/index.m3u8");

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, mediaUri);

        media.TargetDuration.Should().Be(10);
        media.IsEndList.Should().BeTrue();
        media.Segments.Should().HaveCount(3);
        media.Segments.Select(s => s.Uri.ToString()).Should().ContainInOrder(
            "https://cdn.example.com/video/360/seg0.ts",
            "https://cdn.example.com/video/360/seg1.ts",
            "https://cdn.example.com/video/360/seg2.ts");
        media.Segments[0].Duration.Should().BeApproximately(9.009, 0.001);
        media.Segments.Select(s => s.MediaSequence).Should().ContainInOrder(0L, 1L, 2L);
    }

    [Fact]
    public void ParseMedia_HonoursMediaSequenceOffset()
    {
        const string content =
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:42\n#EXTINF:6,\na.ts\n#EXTINF:6,\nb.ts\n#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        media.MediaSequence.Should().Be(42);
        media.Segments.Select(s => s.MediaSequence).Should().ContainInOrder(42L, 43L);
    }

    [Fact]
    public void ParseMedia_ParsesAes128Key_WithExplicitIv()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x00000000000000000000000000000001\n" +
            "#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        HlsEncryption enc = media.Segments[0].Encryption;
        enc.Method.Should().Be(HlsKeyMethod.Aes128);
        enc.Uri.Should().Be(new Uri("https://cdn.example.com/video/key.bin"));
        enc.Iv.Should().NotBeNull();
        enc.Iv!.Should().HaveCount(16);
        enc.Iv![15].Should().Be(1);
    }

    [Fact]
    public void ParseMedia_Aes128WithoutIv_LeavesIvNullForSequenceDerivation()
    {
        const string content =
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:6,\ns.ts\n#EXT-X-ENDLIST\n";

        HlsEncryption enc = M3U8Parser.ParseMedia(content, MasterUri).Segments[0].Encryption;

        enc.Method.Should().Be(HlsKeyMethod.Aes128);
        enc.Iv.Should().BeNull("the IV is derived from the media sequence when omitted");
    }

    [Fact]
    public void ParseMedia_KeyMethodNone_ClearsEncryption()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:6,\nenc.ts\n" +
            "#EXT-X-KEY:METHOD=NONE\n#EXTINF:6,\nclear.ts\n#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        media.Segments[0].Encryption.Method.Should().Be(HlsKeyMethod.Aes128);
        media.Segments[1].Encryption.Method.Should().Be(HlsKeyMethod.None);
    }

    [Fact]
    public void ParseMedia_ParsesExtXMapInitializationSegment()
    {
        // Fragmented-MP4 (CMAF) shape, as Twitter/X serves: the ftyp/moov boxes live in the #EXT-X-MAP
        // initialization segment, and the .m4s fragments are undecodable without it.
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-VERSION:6\n" +
            "#EXT-X-MAP:URI=\"/vid/avc1/0/0/396x270/init.mp4\"\n" +
            "#EXTINF:3.000,\n" +
            "/vid/avc1/0/3000/396x270/a.m4s\n" +
            "#EXTINF:3.000,\n" +
            "/vid/avc1/3000/6000/396x270/b.m4s\n" +
            "#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        media.InitializationSegment.Should().NotBeNull();
        media.InitializationSegment?.Uri.Should().Be(
            new Uri("https://cdn.example.com/vid/avc1/0/0/396x270/init.mp4"));
        media.InitializationSegment?.ByteRange.Should().BeNull("the EXT-X-MAP carries no BYTERANGE here");
        media.Segments.Should().HaveCount(2, "the EXT-X-MAP line is not itself a media segment");
        media.Segments.Select(s => s.Uri.ToString()).Should().ContainInOrder(
            "https://cdn.example.com/vid/avc1/0/3000/396x270/a.m4s",
            "https://cdn.example.com/vid/avc1/3000/6000/396x270/b.m4s");
    }

    [Fact]
    public void ParseMedia_WithoutExtXMap_LeavesInitializationSegmentNull()
    {
        const string content = "#EXTM3U\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n";

        M3U8Parser.ParseMedia(content, MasterUri).InitializationSegment.Should().BeNull(
            "a plain MPEG-TS playlist carries its own headers in every segment");
    }

    [Fact]
    public void ParseMedia_KeepsOnlyTheFirstExtXMap()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"first.mp4\"\n#EXTINF:6,\na.m4s\n" +
            "#EXT-X-MAP:URI=\"second.mp4\"\n#EXTINF:6,\nb.m4s\n#EXT-X-ENDLIST\n";

        M3U8Parser.ParseMedia(content, MasterUri).InitializationSegment?.Uri
            .Should().Be(new Uri("https://cdn.example.com/video/first.mp4"));
    }

    [Fact]
    public void ParseMedia_ParsesByteRanges_OnMapAndSegments_CarryingTheOmittedOffsetForward()
    {
        // Byte-range shape, as Twitter/X and other fMP4 CDNs serve: one resource, sub-ranged per segment.
        // The second segment omits its offset, so it continues at the byte after the first (RFC 8216 §4.3.2.2).
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"all.mp4\",BYTERANGE=\"800@0\"\n" +
            "#EXT-X-BYTERANGE:1000@800\n#EXTINF:3,\nall.mp4\n" +
            "#EXT-X-BYTERANGE:900\n#EXTINF:3,\nall.mp4\n" +
            "#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        var resource = new Uri("https://cdn.example.com/video/all.mp4");
        media.InitializationSegment?.Uri.Should().Be(resource);
        media.InitializationSegment?.ByteRange.Should().Be(new HlsByteRange(800, 0));

        media.Segments.Should().HaveCount(2);
        media.Segments.Should().OnlyContain(s => s.Uri == resource, "all segments share one resource");
        media.Segments[0].ByteRange.Should().Be(new HlsByteRange(1000, 800));
        media.Segments[1].ByteRange.Should().Be(
            new HlsByteRange(900, 1800), "the omitted offset continues after the previous sub-range");
        media.Segments[1].ByteRange?.Last.Should().Be(2699);
    }

    [Fact]
    public void ParseMedia_ByteRangeCarryForward_IsPerResourceUri()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXT-X-BYTERANGE:100@0\n#EXTINF:3,\na.mp4\n" +
            "#EXT-X-BYTERANGE:200@0\n#EXTINF:3,\nb.mp4\n" +
            "#EXT-X-BYTERANGE:50\n#EXTINF:3,\na.mp4\n" +
            "#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        media.Segments[2].ByteRange.Should().Be(
            new HlsByteRange(50, 100), "the carry-forward follows a.mp4, not the intervening b.mp4");
    }

    [Fact]
    public void ParseMedia_ByteRangeOmittingOffsetWithNoPreviousSubRange_Throws()
    {
        // Unresolvable: silently dropping the range would download the whole resource and emit a wrong file.
        const string content = "#EXTM3U\n#EXT-X-BYTERANGE:1000\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n";

        Action act = () => M3U8Parser.ParseMedia(content, MasterUri);

        act.Should().Throw<HlsExtractionException>().WithMessage("*all.mp4*omits its offset*");
    }

    [Fact]
    public void ParseMedia_ByteRangeAfterAnUnrangedSegmentOfTheSameResource_Throws()
    {
        // A whole-resource segment establishes no sub-range to continue from.
        const string content =
            "#EXTM3U\n#EXTINF:3,\nall.mp4\n#EXT-X-BYTERANGE:10\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n";

        Action act = () => M3U8Parser.ParseMedia(content, MasterUri);

        act.Should().Throw<HlsExtractionException>().WithMessage("*omits its offset*");
    }

    [Theory]
    [InlineData("#EXT-X-BYTERANGE:abc@0")]
    [InlineData("#EXT-X-BYTERANGE:0@0")]
    [InlineData("#EXT-X-BYTERANGE:-5@0")]
    [InlineData("#EXT-X-BYTERANGE:100@x")]
    [InlineData("#EXT-X-BYTERANGE:100@-1")]
    [InlineData("#EXT-X-BYTERANGE:")]
    public void ParseMedia_MalformedByteRange_Throws(string byteRangeLine)
    {
        string content = "#EXTM3U\n" + byteRangeLine + "\n#EXTINF:3,\nall.mp4\n#EXT-X-ENDLIST\n";

        Action act = () => M3U8Parser.ParseMedia(content, MasterUri);

        act.Should().Throw<HlsExtractionException>();
    }

    [Fact]
    public void ParseMedia_MalformedByteRangeOnExtXMap_Throws()
    {
        const string content =
            "#EXTM3U\n#EXT-X-MAP:URI=\"all.mp4\",BYTERANGE=\"nonsense\"\n#EXTINF:3,\na.m4s\n#EXT-X-ENDLIST\n";

        Action act = () => M3U8Parser.ParseMedia(content, MasterUri);

        act.Should().Throw<HlsExtractionException>().WithMessage("*usable length*");
    }

    [Fact]
    public void ParseMedia_WithoutByteRanges_LeavesEverySegmentByteRangeNull()
    {
        // Regression guard: the overwhelmingly common playlist shape must be unaffected by byte-range support.
        const string content =
            "#EXTM3U\n#EXTINF:6,\nseg0.ts\n#EXTINF:6,\nseg1.ts\n#EXT-X-ENDLIST\n";

        HlsMediaPlaylist media = M3U8Parser.ParseMedia(content, MasterUri);

        media.Segments.Should().HaveCount(2);
        media.Segments.Should().OnlyContain(s => s.ByteRange == null);
    }

    [Theory]
    [InlineData("0x0A0B", new byte[] { 0x0A, 0x0B })]
    [InlineData("ABCD", new byte[] { 0xAB, 0xCD })]
    [InlineData("0xZZ", null)]
    [InlineData("0xABC", null)]
    public void ParseHex_DecodesOrRejects(string input, byte[]? expected)
    {
        M3U8Parser.ParseHex(input).Should().Equal(expected);
    }
}
