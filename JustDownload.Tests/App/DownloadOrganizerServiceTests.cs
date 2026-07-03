using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// TASK-180: before this, <see cref="IDownloadOrganizer"/> was fully implemented and DI-registered but never
/// invoked from anywhere in the completion pipeline — "Organize by category" in Settings had zero effect on
/// a real download. <see cref="DownloadOrganizerService"/> is what actually calls it now, mirroring
/// <see cref="AutoExtractService"/>'s Completed-transition pattern.
/// </summary>
public sealed class DownloadOrganizerServiceTests
{
    private sealed class FakeManager : IDownloadManager
    {
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

#pragma warning disable CS0067
        public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;
#pragma warning restore CS0067

        public void Raise(long id, DownloadStatus current) =>
            StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, DownloadStatus.Active, current));

        public Task<long> EnqueueAsync(EnqueueDownloadRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DownloadResult> StartAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DownloadResult> RenewAsync(long id, Uri u, CancellationToken ct = default) => throw new NotSupportedException();
        public DownloadProgress? GetProgress(long id) => null;
        public IReadOnlyList<ConnectionStat> GetConnections(long id) => [];
    }

    private static Download CompletedRecord(string directory, string filename, string? categoryType) => new()
    {
        Id = 1,
        Url = "https://example.test/file",
        Status = "complete",
        Directory = directory,
        Filename = filename,
        CategoryType = categoryType,
    };

    private static IDownloadRepository RepoFor(Download record)
    {
        var repo = Substitute.For<IDownloadRepository>();
        repo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Download?>(record));
        return repo;
    }

    [Fact]
    public async Task Completed_WithOrganizeEnabled_MovesFile_AndPersistsTheNewPath()
    {
        Download record = CompletedRecord(@"C:\downloads", "movie.mp4", FileCategory.Video.ToString());
        IDownloadRepository repo = RepoFor(record);
        var organizer = Substitute.For<IDownloadOrganizer>();
        organizer.OrganizeAsync(@"C:\downloads\movie.mp4", FileCategory.Video, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(@"C:\downloads\Video\movie.mp4"));
        var manager = new FakeManager();
        using var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(100); // fire-and-forget async handler

        await repo.Received(1).UpdateAsync(
            Arg.Is<Download>(d => d.Directory == @"C:\downloads\Video" && d.Filename == "movie.mp4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_WhenOrganizerLeavesPathUnchanged_DoesNotTouchTheRepository()
    {
        // Toggle off, or already in the right place — IDownloadOrganizer's own contract returns the
        // original path unchanged in both cases (TASK-046 AC0).
        Download record = CompletedRecord(@"C:\downloads", "movie.mp4", FileCategory.Video.ToString());
        IDownloadRepository repo = RepoFor(record);
        var organizer = Substitute.For<IDownloadOrganizer>();
        organizer.OrganizeAsync(@"C:\downloads\movie.mp4", FileCategory.Video, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(@"C:\downloads\movie.mp4"));
        var manager = new FakeManager();
        using var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(100);

        await repo.DidNotReceive().UpdateAsync(Arg.Any<Download>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_WithNoResolvedCategory_SkipsOrganizing()
    {
        Download record = CompletedRecord(@"C:\downloads", "movie.mp4", categoryType: null);
        IDownloadRepository repo = RepoFor(record);
        var organizer = Substitute.For<IDownloadOrganizer>();
        var manager = new FakeManager();
        using var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(100);

        await organizer.DidNotReceiveWithAnyArgs().OrganizeAsync(default!, default, default);
    }

    [Fact]
    public async Task NonCompletedTransition_IsIgnored()
    {
        var repo = Substitute.For<IDownloadRepository>();
        var organizer = Substitute.For<IDownloadOrganizer>();
        var manager = new FakeManager();
        using var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Failed);
        await Task.Delay(100);

        await repo.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task OrganizerThrowing_IsSwallowed_NotThrown()
    {
        Download record = CompletedRecord(@"C:\downloads", "movie.mp4", FileCategory.Video.ToString());
        IDownloadRepository repo = RepoFor(record);
        var organizer = Substitute.For<IDownloadOrganizer>();
        organizer.OrganizeAsync(Arg.Any<string>(), Arg.Any<FileCategory>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new IOException("file in use"));
        var manager = new FakeManager();
        using var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        Action act = () => manager.Raise(1, DownloadStatus.Completed);

        act.Should().NotThrow();
        await Task.Delay(100);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromStatusChanged()
    {
        var repo = Substitute.For<IDownloadRepository>();
        var organizer = Substitute.For<IDownloadOrganizer>();
        var manager = new FakeManager();
        var service = new DownloadOrganizerService(manager, repo, organizer, NullLogger<DownloadOrganizerService>.Instance);
        service.Start();

        service.Dispose();
        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(100);

        await repo.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }
}
