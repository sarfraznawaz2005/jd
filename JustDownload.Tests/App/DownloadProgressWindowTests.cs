using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Tests for the standalone per-download progress window (TASK-225): the window opens when a download starts
/// or resumes and re-focuses rather than duplicating, the view-model switches to its terminal state with the
/// open-file actions, and "close when done" is honoured and persisted.
/// </summary>
public sealed class DownloadProgressWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 9, 0, 0, TimeSpan.Zero);

    private static Download Record(long id, string status) => new()
    {
        Id = id,
        Url = $"https://host.example/file{id}.bin",
        Filename = $"file{id}.bin",
        Directory = @"C:\Downloads",
        TotalBytes = 1_000_000,
        Status = status,
        CreatedAt = Now,
    };

    private static DownloadRowViewModel Row(long id = 1, string status = DownloadStatusCodes.Active) =>
        new(Record(id, status), Now, FileCategory.Other);

    /// <summary>Wires a real <see cref="DownloadsListViewModel"/> (the row source) to the service under test,
    /// recording every presentation instead of opening real windows.</summary>
    private sealed class Harness
    {
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IDownloadRepository Repository { get; } = Substitute.For<IDownloadRepository>();
        public IFileRevealer Revealer { get; } = Substitute.For<IFileRevealer>();
        public List<DownloadProgressViewModel> Presented { get; } = [];
        public DownloadsListViewModel List { get; }
        public DownloadProgressWindowService Service { get; }

        public Harness(AppSettings? settings = null)
        {
            Settings.Current.Returns(settings ?? new AppSettings());
            Settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>())
                .Returns(Task.FromResult(new AppSettings()));
            Manager.GetConnections(Arg.Any<long>()).Returns([]);
            Repository.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Download>>([]));

            var categorizer = Substitute.For<IFileCategorizer>();
            categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Other);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);

            List = new DownloadsListViewModel(
                Repository, Manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
                Revealer, categorizer, clock);

            Service = new DownloadProgressWindowService(
                Manager, Settings, List, BuildViewModel, Presented.Add);
        }

        public DownloadProgressViewModel BuildViewModel(DownloadRowViewModel row) =>
            new(
                row,
                new DownloadDetailViewModel(Manager, Substitute.For<IDownloadActions>()),
                Revealer,
                Settings);
    }

    [AvaloniaFact]
    public async Task EnsureWindow_OpensOnceThenRefocuses()
    {
        var harness = new Harness();
        harness.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>([Record(1, DownloadStatusCodes.Active)]));
        await harness.List.LoadAsync();

        harness.Service.EnsureWindow(1);
        harness.Service.EnsureWindow(1);

        harness.Presented.Should().HaveCount(2, "a second start re-presents the same window");
        harness.Presented[0].Should().BeSameAs(harness.Presented[1]);
        harness.Service.OpenDownloadIds.Should().ContainSingle().Which.Should().Be(1);
    }

    [AvaloniaFact]
    public void EnsureWindow_DoesNothing_WhenDisabledInSettings()
    {
        var harness = new Harness(new AppSettings { ShowDownloadProgressWindow = false });

        harness.Service.EnsureWindow(1);

        harness.Presented.Should().BeEmpty();
        harness.Service.OpenDownloadIds.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task EnsureWindow_HonoursTheOpenWindowCap()
    {
        var harness = new Harness();
        List<Download> records = [.. Enumerable.Range(1, DownloadProgressWindowService.MaxOpenWindows + 3)
            .Select(i => Record(i, DownloadStatusCodes.Active))];
        harness.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(records));
        await harness.List.LoadAsync();

        foreach (Download record in records)
        {
            harness.Service.EnsureWindow(record.Id);
        }

        harness.Service.OpenDownloadIds.Should().HaveCount(DownloadProgressWindowService.MaxOpenWindows);
    }

    [AvaloniaFact]
    public async Task Forget_AllowsTheWindowToOpenAgain()
    {
        var harness = new Harness();
        harness.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>([Record(1, DownloadStatusCodes.Active)]));
        await harness.List.LoadAsync();

        harness.Service.EnsureWindow(1);
        harness.Service.Forget(harness.Presented[0]);
        harness.Service.OpenDownloadIds.Should().BeEmpty();

        harness.Service.EnsureWindow(1);

        harness.Presented.Should().HaveCount(2);
        harness.Presented[1].Should().NotBeSameAs(harness.Presented[0], "a fresh window is built after a close");
    }

    [AvaloniaFact]
    public void ViewModel_SwitchesToCompleteState_AndOffersOpenActions()
    {
        var harness = new Harness();
        DownloadRowViewModel row = Row();
        DownloadProgressViewModel vm = harness.BuildViewModel(row);

        vm.IsFinished.Should().BeFalse();
        vm.OpenFileCommand.CanExecute(null).Should().BeFalse();

        row.ApplyStatus(DownloadStatus.Completed);

        vm.IsComplete.Should().BeTrue();
        vm.IsFailed.Should().BeFalse();
        vm.OutcomeLabel.Should().Be("Download complete");
        vm.OpenFileCommand.CanExecute(null).Should().BeTrue();

        vm.OpenFileCommand.Execute(null);
        harness.Revealer.Received(1).OpenFile(row.FilePath);
    }

    [AvaloniaFact]
    public void ViewModel_ReportsFailure_WithoutOpenActions()
    {
        var harness = new Harness();
        DownloadRowViewModel row = Row();
        DownloadProgressViewModel vm = harness.BuildViewModel(row);

        row.ApplyStatus(DownloadStatus.Failed);

        vm.IsFinished.Should().BeTrue();
        vm.IsComplete.Should().BeFalse();
        vm.OutcomeLabel.Should().Be("Download failed");
        vm.OpenFileCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public void ViewModel_ClosesOnCompletion_OnlyWhenCloseWhenDoneIsSet()
    {
        var harness = new Harness();
        DownloadRowViewModel stayOpen = Row(1);
        DownloadProgressViewModel keep = harness.BuildViewModel(stayOpen);
        bool keepClosed = false;
        keep.CloseRequested += (_, _) => keepClosed = true;

        stayOpen.ApplyStatus(DownloadStatus.Completed);
        keepClosed.Should().BeFalse("the default is to stay open on the Complete state");

        DownloadRowViewModel autoClose = Row(2);
        DownloadProgressViewModel closing = harness.BuildViewModel(autoClose);
        closing.CloseWhenDone = true;
        bool closingClosed = false;
        closing.CloseRequested += (_, _) => closingClosed = true;

        autoClose.ApplyStatus(DownloadStatus.Completed);
        closingClosed.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ViewModel_PersistsCloseWhenDone()
    {
        var harness = new Harness();
        DownloadProgressViewModel vm = harness.BuildViewModel(Row());

        vm.CloseWhenDone = true;

        harness.Settings.Received().UpdateAsync(Arg.Is<Func<AppSettings, AppSettings>>(
            update => update(new AppSettings()).CloseProgressWindowWhenDone));
    }

    [AvaloniaFact]
    public void ViewModel_SeedsCloseWhenDone_FromSettings()
    {
        var harness = new Harness(new AppSettings { CloseProgressWindowWhenDone = true });

        harness.BuildViewModel(Row()).CloseWhenDone.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ProgressWindow_Mounts_AndBindsItsViewModel()
    {
        var harness = new Harness();
        DownloadRowViewModel row = Row();
        var window = new JustDownload.App.Views.DownloadProgressWindow
        {
            DataContext = harness.BuildViewModel(row),
        };

        window.Show();

        window.Title.Should().Be(row.FileName);
        window.Close();
    }
}
