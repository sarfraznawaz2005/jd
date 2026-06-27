using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Tests for the list's deliberate empty / loading / error / filtered-empty states (TASK-058). Exactly one
/// state is visible at a time, the error state recovers via retry, and an empty filter shows its own state
/// rather than the first-run hint.
/// </summary>
public sealed class ListStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public IDownloadRepository Repository { get; } = Substitute.For<IDownloadRepository>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();

        public DownloadsListViewModel Build()
        {
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Video);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            return new DownloadsListViewModel(
                Repository, Manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
                Substitute.For<IFileRevealer>(), Categorizer, clock);
        }
    }

    private static Download Record(long id, string filename = "v.mp4") => new()
    {
        Id = id,
        Url = $"https://host/{filename}",
        Filename = filename,
        Directory = @"C:\Downloads",
        TotalBytes = 1000,
        Status = DownloadStatusCodes.Active,
        CreatedAt = Now,
    };

    private static void ExactlyOne(DownloadsListViewModel vm)
    {
        new[] { vm.ShowLoading, vm.ShowError, vm.ShowEmptyState, vm.ShowFilteredEmpty, vm.ShowGrid }
            .Count(on => on).Should().Be(1, "exactly one list state is visible at a time");
    }

    [AvaloniaFact]
    public async Task NoDownloads_ShowsEmptyState()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(Array.Empty<Download>()));
        var vm = h.Build();

        await vm.LoadAsync();

        vm.ShowEmptyState.Should().BeTrue();
        vm.ShowGrid.Should().BeFalse();
        ExactlyOne(vm);
    }

    [AvaloniaFact]
    public async Task WithDownloads_ShowsGrid()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1) }));
        var vm = h.Build();

        await vm.LoadAsync();

        vm.ShowGrid.Should().BeTrue();
        ExactlyOne(vm);
    }

    [AvaloniaFact]
    public async Task LoadFailure_ShowsError_ThenRetrySucceeds()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Download>>>(_ => throw new InvalidOperationException("db down"));
        var vm = h.Build();

        await vm.LoadAsync();

        vm.ShowError.Should().BeTrue();
        vm.LoadError.Should().NotBeNullOrEmpty();
        ExactlyOne(vm);

        // Recover: the repository now returns data and retry clears the error.
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1) }));
        await vm.RetryLoadCommand.ExecuteAsync(null);

        vm.ShowError.Should().BeFalse();
        vm.ShowGrid.Should().BeTrue();
        ExactlyOne(vm);
    }

    [AvaloniaFact]
    public async Task FilterHidingEverything_ShowsFilteredEmpty_NotFirstRunHint()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, "v.mp4") }));
        var vm = h.Build();
        await vm.LoadAsync();

        // Everything is Video; filtering to Audio hides all rows.
        vm.ApplyFilter(new DownloadFilter(DownloadFilterKind.Category, FileCategory.Audio));

        vm.ShowFilteredEmpty.Should().BeTrue();
        vm.ShowEmptyState.Should().BeFalse("there are downloads, just none in this view");
        ExactlyOne(vm);
    }

    [AvaloniaFact]
    public async Task CompletingTheLastFilteredRow_DropsBackToFilteredEmpty()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, "v.mp4") }));
        var vm = h.Build();
        await vm.LoadAsync();
        vm.ApplyFilter(new DownloadFilter(DownloadFilterKind.Incomplete));
        vm.ShowGrid.Should().BeTrue("the active download is incomplete");

        h.Manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            h.Manager, new DownloadStatusChangedEventArgs(1, DownloadStatus.Active, DownloadStatus.Completed));
        Dispatcher.UIThread.RunJobs();

        vm.ShowFilteredEmpty.Should().BeTrue("the row left the Incomplete view");
        ExactlyOne(vm);
    }
}
