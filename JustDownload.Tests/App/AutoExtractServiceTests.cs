using System.IO.Compression;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.PostProcess;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Opt-in auto-extract on completion (TASK-135): when the setting is on a finished .zip is unpacked into a
/// sibling folder; when off nothing happens. Extraction runs on a background task, so the tests poll for the
/// result with a bounded timeout.
/// </summary>
public sealed class AutoExtractServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-autoextract-" + Guid.NewGuid().ToString("N"));

    public AutoExtractServiceTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Completed_Zip_WithSettingOn_IsExtracted()
    {
        string zipPath = WriteZip("bundle.zip");
        var manager = new FakeManager();
        using var service = new AutoExtractService(
            manager, RepoFor("bundle.zip"), new ArchiveExtractor(), Settings(on: true),
            NullLogger<AutoExtractService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);

        string destination = Path.Combine(_dir, "bundle");
        string? content = await WaitForContentAsync(Path.Combine(destination, "readme.txt"));
        content.Should().Be("hello");
    }

    [Fact]
    public async Task Completed_Zip_WithSettingOff_IsNotExtracted()
    {
        WriteZip("bundle.zip");
        var manager = new FakeManager();
        using var service = new AutoExtractService(
            manager, RepoFor("bundle.zip"), new ArchiveExtractor(), Settings(on: false),
            NullLogger<AutoExtractService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);

        await Task.Delay(250);
        Directory.Exists(Path.Combine(_dir, "bundle")).Should().BeFalse();
    }

    [Fact]
    public async Task Completed_NonArchive_IsIgnored()
    {
        var manager = new FakeManager();
        using var service = new AutoExtractService(
            manager, RepoFor("video.mp4"), new ArchiveExtractor(), Settings(on: true),
            NullLogger<AutoExtractService>.Instance);
        service.Start();

        manager.Raise(1, DownloadStatus.Completed);

        await Task.Delay(250);
        Directory.GetFileSystemEntries(_dir).Should().BeEmpty();
    }

    private string WriteZip(string name)
    {
        string zipPath = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
        writer.Write("hello");
        return zipPath;
    }

    private IDownloadRepository RepoFor(string filename)
    {
        var repo = Substitute.For<IDownloadRepository>();
        repo.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Download?>(
                new Download { Url = "u", Status = "complete", Directory = _dir, Filename = filename }));
        return repo;
    }

    private static ISettingsService Settings(bool on)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { AutoExtractArchives = on });
        return settings;
    }

    /// <summary>
    /// Polls for <paramref name="path"/> to become both present and readable, returning its content (or
    /// null on timeout). A bare <c>File.Exists</c> poll isn't enough to synchronize with the background
    /// extraction: <c>ZipFile.ExtractToDirectory</c> can make an entry's file exist a moment before its
    /// writer has finished and closed it, so an existence check followed by a separate read can race the
    /// extractor's own still-open handle — observed as a real IOException on macOS CI. Retrying the read
    /// itself (not just the existence check) is what actually synchronizes with "this file is done".
    /// </summary>
    private static async Task<string?> WaitForContentAsync(string path)
    {
        for (int i = 0; i < 60; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    return await File.ReadAllTextAsync(path);
                }
            }
            catch (IOException)
            {
                // Still being written/closed by the extractor — retry.
            }

            await Task.Delay(50);
        }

        return null;
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
