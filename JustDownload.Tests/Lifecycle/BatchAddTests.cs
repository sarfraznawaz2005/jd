using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Batch add (TASK-074, US-16 AC3): the pure expander turns a pasted block plus <c>[a-b]</c> patterns into
/// the concrete URL list (AC1), and the enqueuer registers each valid URL through the manager (AC0).
/// </summary>
public sealed class BatchAddTests
{
    // --- Expander (pure) -------------------------------------------------------------------------

    [Fact]
    public void Expand_SplitsMultipleUrls_AcrossLinesAndSpaces()
    {
        const string text = "https://a/1.bin\nhttps://b/2.bin   https://c/3.bin\n\n# a comment\nhttps://d/4.bin";

        IReadOnlyList<string> urls = BatchUrlExpander.Expand(text);

        urls.Should().Equal(
            "https://a/1.bin", "https://b/2.bin", "https://c/3.bin", "https://d/4.bin");
    }

    [Fact]
    public void ExpandUrl_ZeroPaddedRange_ExpandsWithPadding()
    {
        IReadOnlyList<string> urls = BatchUrlExpander.ExpandUrl("https://x/img[001-100].jpg");

        urls.Should().HaveCount(100);
        urls[0].Should().Be("https://x/img001.jpg");
        urls[8].Should().Be("https://x/img009.jpg");
        urls[9].Should().Be("https://x/img010.jpg");
        urls[99].Should().Be("https://x/img100.jpg");
    }

    [Fact]
    public void ExpandUrl_UnpaddedRange_HasNoPadding()
    {
        IReadOnlyList<string> urls = BatchUrlExpander.ExpandUrl("https://x/p[1-10].html");

        urls[0].Should().Be("https://x/p1.html");
        urls[9].Should().Be("https://x/p10.html");
    }

    [Fact]
    public void ExpandUrl_DescendingRange_CountsDown()
    {
        IReadOnlyList<string> urls = BatchUrlExpander.ExpandUrl("https://x/[3-1].bin");

        urls.Should().Equal("https://x/3.bin", "https://x/2.bin", "https://x/1.bin");
    }

    [Fact]
    public void ExpandUrl_MultipleRanges_AreCartesian()
    {
        IReadOnlyList<string> urls = BatchUrlExpander.ExpandUrl("https://x/[1-2]/[1-3].bin");

        urls.Should().Equal(
            "https://x/1/1.bin", "https://x/1/2.bin", "https://x/1/3.bin",
            "https://x/2/1.bin", "https://x/2/2.bin", "https://x/2/3.bin");
    }

    [Fact]
    public void ExpandUrl_NoRange_ReturnsUnchanged()
    {
        BatchUrlExpander.ExpandUrl("https://x/file.bin").Should().Equal("https://x/file.bin");
    }

    [Fact]
    public void ExpandUrl_TooLarge_Throws()
    {
        Action act = () => BatchUrlExpander.ExpandUrl("https://x/[1-100]/[1-100]/[1-100].bin");

        act.Should().Throw<ArgumentException>();
    }

    // --- Enqueuer --------------------------------------------------------------------------------

    private sealed class RecordingManager : IDownloadManager
    {
        public ConcurrentQueue<EnqueueDownloadRequest> Enqueued { get; } = new();
        private long _next;

        public Task<long> EnqueueAsync(EnqueueDownloadRequest request, CancellationToken ct = default)
        {
            Enqueued.Enqueue(request);
            return Task.FromResult(Interlocked.Increment(ref _next));
        }

#pragma warning disable CS0067
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;
#pragma warning restore CS0067

        public Task<DownloadResult> StartAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DownloadResult> RenewAsync(long id, Uri newUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public DownloadProgress? GetProgress(long id) => null;
        public IReadOnlyList<ConnectionStat> GetConnections(long id) => [];
    }

    private static BatchEnqueuer Build(RecordingManager manager) =>
        new(manager, NullLogger<BatchEnqueuer>.Instance);

    [Fact]
    public async Task EnqueueAsync_EnqueuesEveryValidUrl_WithDerivedNames()
    {
        var manager = new RecordingManager();
        var request = new BatchEnqueueRequest
        {
            Text = "https://x/a.bin\nhttps://x/img[1-3].jpg",
            DestinationDirectory = "/downloads",
            MaxConnections = 8,
        };

        IReadOnlyList<long> ids = await Build(manager).EnqueueAsync(request);

        ids.Should().HaveCount(4); // a.bin + img1/img2/img3
        manager.Enqueued.Select(e => e.Url.ToString()).Should().Equal(
            "https://x/a.bin", "https://x/img1.jpg", "https://x/img2.jpg", "https://x/img3.jpg");
        manager.Enqueued.Select(e => e.FileName).Should().Equal("a.bin", "img1.jpg", "img2.jpg", "img3.jpg");
        manager.Enqueued.Should().OnlyContain(e => e.DestinationDirectory == "/downloads" && e.MaxConnections == 8);
    }

    [Fact]
    public async Task EnqueueAsync_SkipsInvalidAndUnsupportedUrls()
    {
        var manager = new RecordingManager();
        var request = new BatchEnqueueRequest
        {
            Text = "https://x/ok.bin\nnot a url\nmailto:someone@x.com\nftp://h/file.zip\njavascript:alert(1)",
            DestinationDirectory = "/d",
        };

        await Build(manager).EnqueueAsync(request);

        manager.Enqueued.Select(e => e.Url.ToString()).Should().Equal("https://x/ok.bin", "ftp://h/file.zip");
    }

    [Fact]
    public async Task EnqueueAsync_EmptyText_EnqueuesNothing()
    {
        var manager = new RecordingManager();

        IReadOnlyList<long> ids = await Build(manager)
            .EnqueueAsync(new BatchEnqueueRequest { Text = "   \n  \n", DestinationDirectory = "/d" });

        ids.Should().BeEmpty();
        manager.Enqueued.Should().BeEmpty();
    }
}
