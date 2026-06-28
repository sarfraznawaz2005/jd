using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// The download queue (TASK-072, US-16): the concurrency limit is enforced (AC0) and queued downloads start
/// in priority order, with drag-reorder persisted (AC1). Runs against a real temp SQLite repository and a
/// fake manager that mirrors the lifecycle (queued → active → complete) and lets the test gate each run.
/// </summary>
public sealed class DownloadQueueServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;
    private readonly IDownloadRepository _repo;

    public DownloadQueueServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddJustDownloadData();
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
        _repo = _provider.GetRequiredService<IDownloadRepository>();
    }

    private async Task<long> EnqueueAsync(int priority) => await _repo.AddAsync(new Download
    {
        Url = "https://example.com/f.bin",
        Status = DownloadStatusCodes.Queued,
        CreatedAt = new DateTimeOffset(2026, 6, 28, 0, 0, priority % 60, TimeSpan.Zero),
        Priority = priority,
    });

    private static ISettingsService SettingsWithMax(int max)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { MaxConcurrentDownloads = max });
        return settings;
    }

    /// <summary>A manager that advances repo status like the real one and lets the test release each run.</summary>
    private sealed class GatedManager : IDownloadManager
    {
        private readonly IDownloadRepository _repo;
        private readonly object _gate = new();
        private int _active;

        public GatedManager(IDownloadRepository repo) => _repo = repo;

        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

        // Required by the interface but unused by the queue under test.
#pragma warning disable CS0067
        public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;
#pragma warning restore CS0067

        public ConcurrentDictionary<long, TaskCompletionSource> Gates { get; } = new();
        public ConcurrentQueue<long> StartOrder { get; } = new();
        public int PeakConcurrency { get; private set; }

        public async Task<DownloadResult> StartAsync(long id, CancellationToken cancellationToken = default)
        {
            StartOrder.Enqueue(id);
            lock (_gate)
            {
                _active++;
                PeakConcurrency = Math.Max(PeakConcurrency, _active);
            }

            await MarkAsync(id, DownloadStatusCodes.Active, DownloadStatus.Queued, DownloadStatus.Active)
                .ConfigureAwait(false);

            var tcs = Gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _active--;
                }

                await MarkAsync(id, DownloadStatusCodes.Paused, DownloadStatus.Active, DownloadStatus.Paused)
                    .ConfigureAwait(false);
                throw;
            }

            lock (_gate)
            {
                _active--;
            }

            await MarkAsync(id, DownloadStatusCodes.Completed, DownloadStatus.Active, DownloadStatus.Completed)
                .ConfigureAwait(false);

            return new DownloadResult
            {
                TotalBytes = 0,
                FinalUri = new Uri("https://example.com/f.bin"),
                FileName = "f.bin",
                SingleConnection = true,
                InitialSegments = 1,
                Steals = 0,
            };
        }

        public void Release(long id) =>
            Gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        private async Task MarkAsync(long id, string code, DownloadStatus prev, DownloadStatus next)
        {
            Download? d = await _repo.GetAsync(id).ConfigureAwait(false);
            if (d is not null)
            {
                await _repo.UpdateAsync(d with { Status = code }).ConfigureAwait(false);
            }

            StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, prev, next));
        }

        public Task<long> EnqueueAsync(EnqueueDownloadRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DownloadResult> RenewAsync(long id, Uri newUrl, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public DownloadProgress? GetProgress(long id) => null;

        public IReadOnlyList<ConnectionStat> GetConnections(long id) => [];
    }

    private DownloadQueueService BuildQueue(GatedManager manager, int max) =>
        new(manager, _repo, SettingsWithMax(max), NullLogger<DownloadQueueService>.Instance);

    [Fact]
    public async Task StartAsync_NeverRunsMoreThanTheConcurrencyLimit()
    {
        var manager = new GatedManager(_repo);
        using DownloadQueueService queue = BuildQueue(manager, max: 2);
        var ids = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            ids.Add(await EnqueueAsync(priority: 0));
        }

        await queue.StartAsync();

        // Wait until the queue has saturated its 2 slots, then confirm it never exceeds them.
        await WaitUntilAsync(() => queue.RunningIds.Count == 2, TimeSpan.FromSeconds(2));
        queue.RunningIds.Count.Should().Be(2, "the max-concurrent limit caps simultaneous runs");

        // Release everything and let the queue drain.
        foreach (long id in ids)
        {
            manager.Release(id);
        }

        await WaitUntilAsync(() => manager.StartOrder.Count == 5, TimeSpan.FromSeconds(5));
        manager.PeakConcurrency.Should().Be(2, "no more than the limit ever ran at once");
        manager.StartOrder.Should().BeEquivalentTo(ids, "every queued download eventually runs");
    }

    [Fact]
    public async Task StartAsync_StartsInPriorityOrder()
    {
        var manager = new GatedManager(_repo);
        using DownloadQueueService queue = BuildQueue(manager, max: 1);

        long low = await EnqueueAsync(priority: 1);
        long high = await EnqueueAsync(priority: 100);
        long mid = await EnqueueAsync(priority: 50);

        await queue.StartAsync();

        // With one slot, releasing each in turn reveals the start order; it must follow priority desc.
        await WaitUntilAsync(() => manager.StartOrder.Count == 1, TimeSpan.FromSeconds(2));
        manager.Release(high);
        await WaitUntilAsync(() => manager.StartOrder.Count == 2, TimeSpan.FromSeconds(2));
        manager.Release(mid);
        await WaitUntilAsync(() => manager.StartOrder.Count == 3, TimeSpan.FromSeconds(2));
        manager.Release(low);

        manager.StartOrder.Should().Equal(high, mid, low);
    }

    [Fact]
    public async Task ReorderAsync_PersistsPriorities_HighestFirst()
    {
        var manager = new GatedManager(_repo);
        using DownloadQueueService queue = BuildQueue(manager, max: 0); // don't auto-start; just test persistence

        long a = await EnqueueAsync(priority: 0);
        long b = await EnqueueAsync(priority: 0);
        long c = await EnqueueAsync(priority: 0);

        await queue.ReorderAsync([c, a, b]); // c should run first now

        int pc = (await _repo.GetAsync(c))!.Priority;
        int pa = (await _repo.GetAsync(a))!.Priority;
        int pb = (await _repo.GetAsync(b))!.Priority;
        pc.Should().BeGreaterThan(pa);
        pa.Should().BeGreaterThan(pb);

        // The persisted order is what the queue reads back, highest first.
        IReadOnlyList<Download> ordered =
            await _repo.GetByStatusOrderedByPriorityAsync(DownloadStatusCodes.Queued);
        ordered.Select(d => d.Id).Should().Equal(c, a, b);
    }

    [Fact]
    public async Task SetPriorityAsync_PersistsForRestart()
    {
        long id = await EnqueueAsync(priority: 0);
        var manager = new GatedManager(_repo);
        using DownloadQueueService queue = BuildQueue(manager, max: 0);

        await queue.SetPriorityAsync(id, 42);

        (await _repo.GetAsync(id))!.Priority.Should().Be(42);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            await Task.Delay(15);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnectionsCleanup();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void SqliteConnectionsCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
