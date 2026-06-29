using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Download-list serialization (TASK-140): M3U/CSV/JSON round-trip and lenient URL parsing.</summary>
public sealed class DownloadListSerializerTests
{
    private static readonly IReadOnlyList<DownloadListEntry> Sample =
    [
        new DownloadListEntry { Url = "https://a.example/one.bin", FileName = "one.bin" },
        new DownloadListEntry { Url = "https://b.example/two,comma.bin", FileName = "two.bin" },
    ];

    [Theory]
    [InlineData("list.m3u", DownloadListFormat.M3u)]
    [InlineData("LIST.M3U8", DownloadListFormat.M3u)]
    [InlineData("queue.csv", DownloadListFormat.Csv)]
    [InlineData("queue.json", DownloadListFormat.Json)]
    [InlineData("queue.txt", DownloadListFormat.Json)]
    public void DetectFormat_MapsByExtension(string path, DownloadListFormat expected) =>
        DownloadListSerializer.DetectFormat(path).Should().Be(expected);

    [Theory]
    [InlineData(DownloadListFormat.M3u)]
    [InlineData(DownloadListFormat.Csv)]
    [InlineData(DownloadListFormat.Json)]
    public void SerializeThenParse_RoundTripsTheUrls(DownloadListFormat format)
    {
        string content = DownloadListSerializer.Serialize(Sample, format);

        IReadOnlyList<string> urls = DownloadListSerializer.ParseUrls(content, format);

        urls.Should().Equal("https://a.example/one.bin", "https://b.example/two,comma.bin");
    }

    [Fact]
    public void M3u_Serialize_HasExtM3uHeader_AndExtInfPerEntry()
    {
        string content = DownloadListSerializer.Serialize(Sample, DownloadListFormat.M3u);

        content.Should().StartWith("#EXTM3U");
        content.Should().Contain("#EXTINF:-1,one.bin");
    }

    [Fact]
    public void ParseM3u_IgnoresCommentsAndBlankLines()
    {
        const string m3u = "#EXTM3U\n\n#EXTINF:-1,clip\nhttps://x.example/a.mp4\n   \nhttps://x.example/b.mp4\n";

        DownloadListSerializer.ParseUrls(m3u, DownloadListFormat.M3u).Should()
            .Equal("https://x.example/a.mp4", "https://x.example/b.mp4");
    }

    [Fact]
    public void ParseCsv_SkipsHeader_AndReadsFirstColumn()
    {
        const string csv = "url,filename\nhttps://x.example/a.bin,a.bin\n\"https://x.example/b,c.bin\",b.bin\n";

        DownloadListSerializer.ParseUrls(csv, DownloadListFormat.Csv).Should()
            .Equal("https://x.example/a.bin", "https://x.example/b,c.bin");
    }

    [Fact]
    public void ParseJson_Malformed_ReturnsEmpty_NotThrow()
    {
        DownloadListSerializer.ParseUrls("{ not valid json", DownloadListFormat.Json).Should().BeEmpty();
        DownloadListSerializer.ParseUrls("", DownloadListFormat.Json).Should().BeEmpty();
    }
}
