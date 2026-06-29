using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Queue import/export (TASK-140): export writes the current downloads to a file (format by extension), and
/// import parses the URLs and feeds them to the shared batch enqueuer with the chosen destination folder.
/// </summary>
public sealed class DownloadListTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-list-" + Guid.NewGuid().ToString("N"));
    private readonly IDownloadRepository _repository = Substitute.For<IDownloadRepository>();
    private readonly IBatchEnqueuer _batch = Substitute.For<IBatchEnqueuer>();

    public DownloadListTransferTests() => Directory.CreateDirectory(_dir);

    private DownloadListTransfer Transfer() => new(_repository, _batch);

    [Fact]
    public async Task ExportThenImport_RoundTripsThroughTheFile_AndEnqueues()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download { Id = 1, Url = "https://x.example/a.bin", Status = "complete", Filename = "a.bin" },
            new Download { Id = 2, Url = "https://x.example/b.bin", Status = "queued", Filename = "b.bin" },
        });
        string path = Path.Combine(_dir, "queue.json");

        int exported = await Transfer().ExportAsync(path);

        exported.Should().Be(2);
        File.Exists(path).Should().BeTrue();

        await Transfer().ImportAsync(path, _dir);

        await _batch.Received(1).EnqueueAsync(
            Arg.Is<BatchEnqueueRequest>(r =>
                r.DestinationDirectory == _dir
                && r.Text.Contains("https://x.example/a.bin", StringComparison.Ordinal)
                && r.Text.Contains("https://x.example/b.bin", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_EmptyOrUrllessFile_DoesNotEnqueue()
    {
        string path = Path.Combine(_dir, "empty.m3u");
        await File.WriteAllTextAsync(path, "#EXTM3U\n# just comments\n");

        IReadOnlyList<long> ids = await Transfer().ImportAsync(path, _dir);

        ids.Should().BeEmpty();
        await _batch.DidNotReceive().EnqueueAsync(Arg.Any<BatchEnqueueRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAsync_Csv_WritesParsableUrls()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download { Id = 1, Url = "https://x.example/a.bin", Status = "queued", Filename = "a.bin" },
        });
        string path = Path.Combine(_dir, "queue.csv");

        await Transfer().ExportAsync(path);

        string content = await File.ReadAllTextAsync(path);
        DownloadListSerializer.ParseUrls(content, DownloadListFormat.Csv).Should()
            .ContainSingle().Which.Should().Be("https://x.example/a.bin");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
