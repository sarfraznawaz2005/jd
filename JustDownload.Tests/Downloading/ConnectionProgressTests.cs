using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Tests that the segmented downloader reports per-connection progress (TASK-054): a multi-connection
/// download emits reports for every connection id and finishes each with a completion marker, and a
/// range-less download reports a single connection.
/// </summary>
public sealed class ConnectionProgressTests : IDisposable
{
    private readonly string _tempDir;

    public ConnectionProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-conn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string Dest(string name) => Path.Combine(_tempDir, name);

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 31 + 7) % 256);
        }

        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task MultiConnection_ReportsEveryConnection_AndCompletesEach()
    {
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(60),
        };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        var reports = new ConcurrentBag<ConnectionProgress>();
        var sink = new SynchronousProgress<ConnectionProgress>(reports.Add);

        await downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("f.bin"), DestinationPath = Dest("multi.bin"), Connections = 4 },
            progress: null,
            received: null,
            connectionProgress: sink);

        reports.Should().NotBeEmpty();
        reports.Select(r => r.ConnectionId).Distinct().Should().HaveCount(4, "all four connections report progress");
        reports.Where(r => r.IsComplete).Select(r => r.ConnectionId).Distinct()
            .Should().HaveCount(4, "each connection reports a completion marker");
        // Every report stays within its segment bounds.
        reports.Should().OnlyContain(r => r.Position >= r.Start && r.SegmentDownloaded >= 0);
    }

    [Fact]
    public async Task RangeLessServer_ReportsSingleConnection()
    {
        byte[] body = Bytes(64 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        var reports = new ConcurrentBag<ConnectionProgress>();
        var sink = new SynchronousProgress<ConnectionProgress>(reports.Add);

        await downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("f.bin"), DestinationPath = Dest("single.bin"), Connections = 4 },
            progress: null,
            received: null,
            connectionProgress: sink);

        reports.Select(r => r.ConnectionId).Distinct().Should().ContainSingle().Which.Should().Be(0);
        reports.Should().Contain(r => r.IsComplete);
    }

    /// <summary>A direct <see cref="IProgress{T}"/> that invokes the callback synchronously on the reporting thread.</summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
