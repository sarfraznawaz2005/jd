using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Post-download command hook (TASK-136): a configured program runs on completion with the file path passed
/// as a single argument (no shell); nothing runs when the hook is empty or the download didn't complete.
/// </summary>
public sealed class PostDownloadCommandServiceTests
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

    private static IDownloadRepository RepoWith(string directory, string filename)
    {
        var repo = Substitute.For<IDownloadRepository>();
        repo.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Download?>(
                new Download { Url = "u", Status = "complete", Directory = directory, Filename = filename }));
        return repo;
    }

    private static ISettingsService Settings(string? command)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { OnCompletionCommand = command });
        return settings;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Completed_WithCommandSet_LaunchesItWithTheFilePathAsArgument()
    {
        var manager = new FakeManager();
        var launcher = Substitute.For<IProcessLauncher>();
        using var service = new PostDownloadCommandService(
            manager, RepoWith(@"C:\Downloads", "movie.mkv"), Settings(@"C:\Tools\scan.exe"), launcher,
            NullLogger<PostDownloadCommandService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);

        await WaitForAsync(() => launcher.ReceivedCalls().Any());
        launcher.Received(1).Launch(
            @"C:\Tools\scan.exe",
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == Path.Combine(@"C:\Downloads", "movie.mkv")));
    }

    [Fact]
    public async Task Completed_WithNoCommand_DoesNotLaunch()
    {
        var manager = new FakeManager();
        var launcher = Substitute.For<IProcessLauncher>();
        using var service = new PostDownloadCommandService(
            manager, RepoWith(@"C:\Downloads", "movie.mkv"), Settings(command: null), launcher,
            NullLogger<PostDownloadCommandService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);

        await Task.Delay(60);
        launcher.DidNotReceive().Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task NonCompletedTransition_DoesNotLaunch()
    {
        var manager = new FakeManager();
        var launcher = Substitute.For<IProcessLauncher>();
        using var service = new PostDownloadCommandService(
            manager, RepoWith(@"C:\Downloads", "movie.mkv"), Settings(@"C:\Tools\scan.exe"), launcher,
            NullLogger<PostDownloadCommandService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Failed);
        manager.Raise(1, DownloadStatus.Paused);

        await Task.Delay(60);
        launcher.DidNotReceive().Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
    }
}
