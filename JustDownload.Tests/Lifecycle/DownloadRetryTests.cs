using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Fakes;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Auto-retry on transient failures (TASK-131): a transient (network) failure is retried with backoff and
/// resumes, the retry count is persisted, a permanent failure is not retried, and retries are bounded by the
/// configured budget. The real probe runs against a loopback server while a flaky downloader drives the
/// failure pattern; the backoff is zero-delay so the tests do not wait.
/// </summary>
public sealed class DownloadRetryTests : IDisposable
{
    private readonly string _tempDir;

    public DownloadRetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private sealed class FlakyDownloader : ISegmentedDownloader
    {
        private readonly int _failTimes;
        private readonly Exception _failure;
        private int _attempts;

        public FlakyDownloader(int failTimes, Exception failure)
        {
            _failTimes = failTimes;
            _failure = failure;
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            IProgress<long>? progress = null,
            ReceivedRanges? received = null,
            IProgress<ConnectionProgress>? connectionProgress = null,
            ConnectionController? connections = null,
            CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            await Task.Yield();
            if (attempt <= _failTimes)
            {
                throw _failure;
            }

            progress?.Report(1000);
            return new DownloadResult
            {
                TotalBytes = 1000,
                FinalUri = request.Url,
                FileName = Path.GetFileName(request.DestinationPath),
                SingleConnection = false,
                InitialSegments = 4,
                Steals = 0,
            };
        }
    }

    private (ServiceProvider Provider, FlakyDownloader Downloader) Build(
        int maxRetries, int failTimes, Exception failure)
    {
        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var downloader = new FlakyDownloader(failTimes, failure);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        // Swap in the flaky downloader and a zero-delay retry budget (last registration wins).
        services.AddSingleton<ISegmentedDownloader>(downloader);
        services.AddSingleton<IRetryBackoff>(new FakeRetryBackoff(maxRetries));

        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Migrate();
        return (provider, downloader);
    }

    private async Task<long> EnqueueAsync(ServiceProvider provider, LoopbackHttpServer server)
    {
        var manager = provider.GetRequiredService<IDownloadManager>();
        return await manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 4,
        });
    }

    [Fact]
    public async Task TransientFailure_RetriesWithBackoff_ThenSucceeds_AndPersistsRetryCount()
    {
        await using var server = new LoopbackHttpServer { Body = new byte[1000], SupportRanges = true };
        (ServiceProvider provider, FlakyDownloader downloader) =
            Build(maxRetries: 5, failTimes: 2, failure: new IOException("connection reset"));
        using ServiceProvider _ = provider;

        var manager = provider.GetRequiredService<IDownloadManager>();
        var repository = provider.GetRequiredService<IDownloadRepository>();
        long id = await EnqueueAsync(provider, server);

        DownloadResult result = await manager.StartAsync(id);

        result.TotalBytes.Should().Be(1000);
        downloader.Attempts.Should().Be(3, "two transient failures then a success");

        Download? saved = await repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Completed);
        saved.RetryCount.Should().Be(2, "the two retries are persisted");
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        await using var server = new LoopbackHttpServer { Body = new byte[1000], SupportRanges = true };
        (ServiceProvider provider, FlakyDownloader downloader) =
            Build(maxRetries: 5, failTimes: 99, failure: new InvalidOperationException("permanent"));
        using ServiceProvider _ = provider;

        var manager = provider.GetRequiredService<IDownloadManager>();
        var repository = provider.GetRequiredService<IDownloadRepository>();
        long id = await EnqueueAsync(provider, server);

        Func<Task> act = async () => await manager.StartAsync(id);
        await act.Should().ThrowAsync<InvalidOperationException>();

        downloader.Attempts.Should().Be(1, "a permanent failure is not retried");
        Download? saved = await repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Failed);
        saved.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task TransientFailure_ExhaustsRetryBudget_ThenFails()
    {
        await using var server = new LoopbackHttpServer { Body = new byte[1000], SupportRanges = true };
        (ServiceProvider provider, FlakyDownloader downloader) =
            Build(maxRetries: 2, failTimes: 99, failure: new IOException("still down"));
        using ServiceProvider _ = provider;

        var manager = provider.GetRequiredService<IDownloadManager>();
        var repository = provider.GetRequiredService<IDownloadRepository>();
        long id = await EnqueueAsync(provider, server);

        Func<Task> act = async () => await manager.StartAsync(id);
        await act.Should().ThrowAsync<IOException>();

        downloader.Attempts.Should().Be(3, "initial attempt plus two retries");
        Download? saved = await repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Failed);
        saved.RetryCount.Should().Be(2, "the budget of two retries is recorded");
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
