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
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, DownloadStatusCodes.Paused) }));
        var vm = h.Build();
        await vm.LoadAsync();

        vm.ResumeCommand.CanExecute(null).Should().BeFalse("nothing is selected yet");

        vm.SelectedDownload = vm.Downloads[0];
        vm.ResumeCommand.CanExecute(null).Should().BeTrue("a paused download can resume");
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
}
