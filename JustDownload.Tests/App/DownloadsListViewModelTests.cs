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
/// Headless tests for the downloads list (TASK-051): it loads persisted rows, stays live from the manager's
/// status/progress events, and routes the context-menu commands to the action surface with correct
/// per-selection enablement.
/// </summary>
public sealed class DownloadsListViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public IDownloadRepository Repository { get; } = Substitute.For<IDownloadRepository>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IDownloadActions Actions { get; } = Substitute.For<IDownloadActions>();
        public IClipboardService Clipboard { get; } = Substitute.For<IClipboardService>();
        public IFileRevealer Revealer { get; } = Substitute.For<IFileRevealer>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();
        public IClock Clock { get; } = Substitute.For<IClock>();

        public Harness()
        {
            Clock.UtcNow.Returns(Now);
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Other);
            Repository.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Download>>(Array.Empty<Download>()));
        }

        public DownloadsListViewModel Build() =>
            new(Repository, Manager, Actions, Clipboard, Revealer, Categorizer, Clock);
    }

    private static Download Record(long id, string status = DownloadStatusCodes.Paused) => new()
    {
        Id = id,
        Url = $"https://host{id}.example/file{id}.bin",
        Filename = $"file{id}.bin",
        Directory = @"C:\Downloads",
        TotalBytes = 1_000_000,
        Status = status,
        CreatedAt = Now - TimeSpan.FromMinutes(id),
    };

    [AvaloniaFact]
    public async Task LoadAsync_BuildsRowsAndAppliesInSessionProgress()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1), Record(2) }));
        h.Manager.GetProgress(1).Returns(DownloadProgress.Create(
            DownloadStatus.Paused, 740_000, 1_000_000, 0, resumable: true));

        var vm = h.Build();
        await vm.LoadAsync();

        vm.Downloads.Should().HaveCount(2);
        vm.HasDownloads.Should().BeTrue();
        vm.Downloads[0].StatusLabel.Should().Be("Paused · 74%");
    }

    [AvaloniaFact]
    public async Task LoadAsync_AutoSelectsTheFirstRow_SoTheDetailPaneHasSomethingToShow()
    {
        // Otherwise "Toggle details" looked broken on a fresh app: it only affects the detail pane once
        // something is selected, and a first-time user had nothing selected to notice the difference
        // (user-reported).
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1), Record(2) }));

        var vm = h.Build();
        await vm.LoadAsync();

        vm.SelectedDownload.Should().BeSameAs(vm.Downloads[0]);
    }

    [AvaloniaFact]
    public async Task LoadAsync_WithNoDownloads_LeavesSelectionNull()
    {
        var h = new Harness();
        var vm = h.Build();

        await vm.LoadAsync();

        vm.SelectedDownload.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task EnqueueEvent_InsertsRowAtTop()
    {
        var h = new Harness();
        var vm = h.Build();
        await vm.LoadAsync();
        h.Repository.GetAsync(5, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Download?>(Record(5)));

        h.Manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            h.Manager, new DownloadStatusChangedEventArgs(5, null, DownloadStatus.Queued));
        Dispatcher.UIThread.RunJobs();

        vm.Downloads.Should().ContainSingle();
        vm.Downloads[0].Id.Should().Be(5);
        vm.HasDownloads.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task EnqueueEvent_RepositoryFailure_SurfacesError()
    {
        // The enqueue row-add is a fire-and-forget continuation of a manager event; if the repository read
        // fails it must surface, not silently drop the row (TASK-119).
        var h = new Harness();
        var vm = h.Build();
        await vm.LoadAsync();
        h.Repository.GetAsync(5, Arg.Any<CancellationToken>())
            .Returns<Task<Download?>>(_ => throw new InvalidOperationException("db unavailable"));

        h.Manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            h.Manager, new DownloadStatusChangedEventArgs(5, null, DownloadStatus.Queued));
        Dispatcher.UIThread.RunJobs();

        vm.LoadError.Should().NotBeNull("a failed enqueue load surfaces an error instead of silently dropping");
        vm.ShowError.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ProgressEvent_UpdatesTheMatchingRow()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1) }));
        var vm = h.Build();
        await vm.LoadAsync();

        h.Manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            h.Manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 330_000, 1_000_000, 442_000, resumable: true)));
        Dispatcher.UIThread.RunJobs();

        vm.Downloads[0].IsDownloading.Should().BeTrue();
        vm.Downloads[0].StatusLabel.Should().Be("Downloading · 33%");
    }

    [AvaloniaFact]
    public async Task Commands_ReflectSelectionState()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(Array.Empty<Download>()));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.ResumeCommand.CanExecute(null).Should().BeFalse("nothing is selected yet");

        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, DownloadStatusCodes.Paused) }));
        await vm.LoadAsync();

        vm.ResumeCommand.CanExecute(null).Should().BeTrue("the first row is auto-selected on load, and it can resume");
        vm.PauseCommand.CanExecute(null).Should().BeFalse("a paused download cannot be paused");
        vm.RemoveCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ResumeAndPauseCommands_DriveTheActionSurface()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(9, DownloadStatusCodes.Paused) }));
        var vm = h.Build();
        await vm.LoadAsync();
        vm.SelectedDownload = vm.Downloads[0];

        vm.ResumeCommand.Execute(null);
        h.Actions.Received(1).Start(9);

        // Simulate it going active so Pause becomes available, then pause.
        vm.Downloads[0].ApplyStatus(DownloadStatus.Active);
        vm.PauseCommand.CanExecute(null).Should().BeTrue();
        vm.PauseCommand.Execute(null);
        h.Actions.Received(1).Pause(9);
    }

    [AvaloniaFact]
    public async Task RemoveCommand_RemovesRowAndDeletesRecord()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(3) }));
        h.Actions.RemoveAsync(3, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var vm = h.Build();
        await vm.LoadAsync();
        vm.SelectedDownload = vm.Downloads[0];

        await vm.RemoveCommand.ExecuteAsync(null);

        await h.Actions.Received(1).RemoveAsync(3, Arg.Any<CancellationToken>());
        vm.Downloads.Should().BeEmpty();
        vm.HasDownloads.Should().BeFalse();
        vm.SelectedDownload.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task CopyLinkAndOpenFolderCommands_UseTheirServices()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(4) }));
        var vm = h.Build();
        await vm.LoadAsync();
        vm.SelectedDownload = vm.Downloads[0];

        await vm.CopyLinkCommand.ExecuteAsync(null);
        await h.Clipboard.Received(1).CopyAsync("https://host4.example/file4.bin");

        vm.OpenFolderCommand.Execute(null);
        h.Revealer.Received(1).RevealInFolder(Path.Combine(@"C:\Downloads", "file4.bin"));
    }

    [AvaloniaFact]
    public async Task StopAll_PausesEveryActiveDownload_AndIsEnabledOnlyWhenAnyActive()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[]
            {
                Record(1, DownloadStatusCodes.Active),
                Record(2, DownloadStatusCodes.Paused),
                Record(3, DownloadStatusCodes.Active),
            }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.StopAllCommand.CanExecute(null).Should().BeTrue("two downloads are active");

        vm.StopAllCommand.Execute(null);
        h.Actions.Received(1).Pause(1);
        h.Actions.Received(1).Pause(3);
        h.Actions.DidNotReceive().Pause(2);
    }

    [AvaloniaFact]
    public async Task StopAll_IsDisabled_WhenNothingIsActive()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, DownloadStatusCodes.Paused) }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.StopAllCommand.CanExecute(null).Should().BeFalse();

        // A progress event flipping it active should enable Stop-all.
        h.Manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            h.Manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 1000, 1_000_000, 442_000, resumable: true)));
        Dispatcher.UIThread.RunJobs();

        vm.StopAllCommand.CanExecute(null).Should().BeTrue("the download became active");
    }

    [AvaloniaFact]
    public async Task RenewCommand_RaisesRenewRequestedForExpired()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(6, DownloadStatusCodes.Expired) }));
        var vm = h.Build();
        await vm.LoadAsync();
        vm.SelectedDownload = vm.Downloads[0];

        DownloadRowViewModel? requested = null;
        vm.RenewRequested += (_, row) => requested = row;

        vm.RenewCommand.CanExecute(null).Should().BeTrue();
        vm.RenewCommand.Execute(null);

        requested.Should().BeSameAs(vm.Downloads[0]);
    }

    // --- Search & filter (TASK-134) --------------------------------------------------------------

    [AvaloniaFact]
    public async Task SearchQuery_FiltersByFileName()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1), Record(2), Record(3) }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.SearchQuery = "file2";

        vm.Downloads.Should().ContainSingle().Which.FileName.Should().Be("file2.bin");

        vm.SearchQuery = string.Empty; // clearing restores all
        vm.Downloads.Should().HaveCount(3);
    }

    [AvaloniaFact]
    public async Task SearchQuery_FiltersByUrl_CaseInsensitive()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1), Record(2) }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.SearchQuery = "HOST2.EXAMPLE";

        vm.Downloads.Should().ContainSingle().Which.Url.Should().Contain("host2.example");
    }

    [AvaloniaFact]
    public async Task StatusFilter_ShowsOnlyMatchingStatus()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[]
            {
                Record(1, DownloadStatusCodes.Completed),
                Record(2, DownloadStatusCodes.Paused),
                Record(3, DownloadStatusCodes.Completed),
            }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.StatusFilter = DownloadStatusFilter.Completed;

        vm.Downloads.Should().HaveCount(2);
        vm.Downloads.Should().OnlyContain(r => r.IsCompleted);
    }

    [AvaloniaFact]
    public async Task DateFilter_ShowsOnlyRecentDownloads()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[]
            {
                Record(1), // CreatedAt = Now - 1 min → today
                Record(2) with { CreatedAt = Now - TimeSpan.FromDays(10) }, // older than a week
            }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.DateFilter = DownloadDateFilter.Today;
        vm.Downloads.Should().ContainSingle().Which.FileName.Should().Be("file1.bin");

        vm.DateFilter = DownloadDateFilter.Last30Days;
        vm.Downloads.Should().HaveCount(2, "both fall within 30 days");
    }

    [AvaloniaFact]
    public async Task Search_CombinesWithSidebarFilter()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[]
            {
                Record(1, DownloadStatusCodes.Completed),
                Record(2, DownloadStatusCodes.Paused),
            }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.ApplyFilter(new DownloadFilter(DownloadFilterKind.Incomplete));
        vm.SearchQuery = "file"; // matches both names, but the sidebar limits to incomplete

        vm.Downloads.Should().ContainSingle().Which.FileName.Should().Be("file2.bin");
    }

    // --- Incremental counts (TASK-108) -----------------------------------------------------------

    [AvaloniaFact]
    public async Task Counts_UpdateIncrementally_OnStatusChange_AndRemove()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[]
            {
                Record(1, DownloadStatusCodes.Completed),
                Record(2, DownloadStatusCodes.Paused),
            }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.Counts.All.Should().Be(2);
        vm.Counts.Completed.Should().Be(1);
        vm.Counts.Incomplete.Should().Be(1);

        // The paused download completes: the split shifts, the total is unchanged.
        h.Manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            h.Manager, new DownloadStatusChangedEventArgs(2, DownloadStatus.Paused, DownloadStatus.Completed));
        Dispatcher.UIThread.RunJobs();
        vm.Counts.Completed.Should().Be(2);
        vm.Counts.Incomplete.Should().Be(0);

        // Removing the (completed) download drops the total and the completed count.
        vm.SelectedDownload = vm.Downloads.First(r => r.Id == 1);
        await vm.RemoveCommand.ExecuteAsync(null);
        vm.Counts.All.Should().Be(1);
        vm.Counts.Completed.Should().Be(1, "the one still present is the now-completed #2");
        vm.Counts.Incomplete.Should().Be(0);
    }
}
