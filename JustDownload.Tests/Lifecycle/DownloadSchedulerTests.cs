using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// The scheduler (TASK-073, US-16): a scheduled start/stop drives the queue at the set time and a past time
/// fires immediately (AC0); when opted in, the queue draining triggers a one-shot shutdown/sleep (AC1).
/// Driven with fakes so no real machine is ever powered off.
/// </summary>
public sealed class DownloadSchedulerTests
{
    private sealed class FakeQueue : IDownloadQueueService
    {
        public int StartCalls;
        public int StopCalls;
        public List<long> Running { get; } = [];

        public IReadOnlyCollection<long> RunningIds => Running;

        public Task StartAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref StartCalls);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref StopCalls);
            return Task.CompletedTask;
        }

        public Task SetPriorityAsync(long id, int priority, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReorderAsync(IReadOnlyList<long> orderedIdsHighestFirst, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeManager : IDownloadManager
    {
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

#pragma warning disable CS0067
        public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;
#pragma warning restore CS0067

        public void Raise(DownloadStatus current) =>
            StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(1, DownloadStatus.Active, current));

        public Task<long> EnqueueAsync(EnqueueDownloadRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DownloadResult> StartAsync(long id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DownloadResult> RenewAsync(long id, Uri newUrl, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public DownloadProgress? GetProgress(long id) => null;

        public IReadOnlyList<ConnectionStat> GetConnections(long id) => [];
    }

    private sealed class FakeRepo : IDownloadRepository
    {
        public List<Download> Queued { get; } = [];

        public Task<IReadOnlyList<Download>> GetByStatusOrderedByPriorityAsync(string statusCode, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Download>>(Queued.ToArray());

        public Task<long> AddAsync(Download d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download?> GetAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Download>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Download d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetPriorityAsync(long id, int priority, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePower : ISystemPowerController
    {
        public int Shutdowns;
        public int Sleeps;

        public Task ShutdownAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Shutdowns);
            return Task.CompletedTask;
        }

        public Task SleepAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Sleeps);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPower : ISystemPowerController
    {
        public Task ShutdownAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("no permission to power off");

        public Task SleepAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("no permission to sleep");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record Harness(
        DownloadScheduler Scheduler, FakeQueue Queue, FakeManager Manager, FakeRepo Repo, FakePower Power, TestClock Clock);

    private static Harness Build()
    {
        var queue = new FakeQueue();
        var manager = new FakeManager();
        var repo = new FakeRepo();
        var power = new FakePower();
        var clock = new TestClock();
        var scheduler = new DownloadScheduler(
            queue, manager, repo, power, clock, NullLogger<DownloadScheduler>.Instance);
        return new Harness(scheduler, queue, manager, repo, power, clock);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition())
        {
            await Task.Delay(15);
        }
    }

    [Fact]
    public async Task ScheduleStart_PastTime_StartsQueueImmediately()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;

        scheduler.ScheduleStart(h.Clock.UtcNow); // now → fire immediately

        await WaitUntilAsync(() => h.Queue.StartCalls > 0, TimeSpan.FromSeconds(2));
        h.Queue.StartCalls.Should().Be(1);
        scheduler.StartAt.Should().Be(h.Clock.UtcNow);
    }

    [Fact]
    public async Task ScheduleStop_FutureTime_StopsQueueAfterDelay()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;

        scheduler.ScheduleStop(h.Clock.UtcNow + TimeSpan.FromMilliseconds(120));

        h.Queue.StopCalls.Should().Be(0, "the stop time has not arrived yet");
        await WaitUntilAsync(() => h.Queue.StopCalls > 0, TimeSpan.FromSeconds(2));
        h.Queue.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_PreventsAScheduledStart()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;

        scheduler.ScheduleStart(h.Clock.UtcNow + TimeSpan.FromMilliseconds(300));
        scheduler.Cancel();

        await Task.Delay(400);
        h.Queue.StartCalls.Should().Be(0, "a cancelled schedule does not fire");
    }

    [Fact]
    public async Task QueueDrained_WithShutdownAction_ShutsDownOnce()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;
        scheduler.CompletionAction = QueueCompletionAction.Shutdown;
        // Queue is empty: nothing running, nothing queued.

        h.Manager.Raise(DownloadStatus.Completed);
        await WaitUntilAsync(() => h.Power.Shutdowns > 0, TimeSpan.FromSeconds(2));

        h.Manager.Raise(DownloadStatus.Completed); // another terminal event must not power off twice
        await Task.Delay(100);

        h.Power.Shutdowns.Should().Be(1, "the completion action fires exactly once per scheduled session");
        h.Power.Sleeps.Should().Be(0);
    }

    [Fact]
    public async Task QueueDrained_WithSleepAction_Sleeps()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;
        scheduler.CompletionAction = QueueCompletionAction.Sleep;

        h.Manager.Raise(DownloadStatus.Completed);
        await WaitUntilAsync(() => h.Power.Sleeps > 0, TimeSpan.FromSeconds(2));

        h.Power.Sleeps.Should().Be(1);
    }

    [Fact]
    public async Task CompletionAction_NotTaken_WhenWorkRemains()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;
        scheduler.CompletionAction = QueueCompletionAction.Shutdown;
        h.Repo.Queued.Add(new Download { Url = "u", Status = DownloadStatusCodes.Queued }); // still queued work

        h.Manager.Raise(DownloadStatus.Completed);
        await Task.Delay(150);

        h.Power.Shutdowns.Should().Be(0, "the queue is not drained while work remains");
    }

    [Fact]
    public async Task CompletionActionFailure_IsCaughtAndLogged()
    {
        var queue = new FakeQueue();
        var manager = new FakeManager();
        var repo = new FakeRepo();
        var power = new ThrowingPower();
        var logger = new RecordingLogger<DownloadScheduler>();
        using var scheduler = new DownloadScheduler(queue, manager, repo, power, new TestClock(), logger);
        scheduler.CompletionAction = QueueCompletionAction.Shutdown; // queue is empty, so it will try to power off

        manager.Raise(DownloadStatus.Completed);
        await WaitUntilAsync(() => logger.Levels.Contains(LogLevel.Error), TimeSpan.FromSeconds(2));

        logger.Levels.Should().Contain(LogLevel.Error,
            "a failing completion action is caught and surfaced, not left as an unobserved exception");
    }

    [Fact]
    public async Task CompletionAction_None_NeverPowersOff()
    {
        Harness h = Build();
        using DownloadScheduler scheduler = h.Scheduler;
        // CompletionAction defaults to None.

        h.Manager.Raise(DownloadStatus.Completed);
        await Task.Delay(150);

        h.Power.Shutdowns.Should().Be(0);
        h.Power.Sleeps.Should().Be(0);
    }
}
