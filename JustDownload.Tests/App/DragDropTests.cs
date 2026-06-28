using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Drag-and-drop support (TASK-062, US-14): the dropped-link parser extracts an enqueueable URL from
/// dropped text (the cross-platform core of "drop a link to enqueue", AC0), and the shell raises a
/// download request for it. The live OS drag events and the macOS Finder drag-out (AC1) are platform/desktop
/// behaviours verified by manual smoke-testing on macOS, not headless here.
/// </summary>
public sealed class DragDropTests
{
    [Theory]
    [InlineData("https://example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("ftp://host/f.bin", "ftp://host/f.bin")]
    [InlineData("# comment\nhttps://example.com/a.mp4", "https://example.com/a.mp4")]
    [InlineData("  https://example.com/b.mp4  ", "https://example.com/b.mp4")]
    public void TryExtractUrl_ReturnsFirstUsableUrl(string text, string expected)
    {
        DroppedLinkParser.TryExtractUrl(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtractUrl_RejectsNonDownloadableText(string? text)
    {
        DroppedLinkParser.TryExtractUrl(text).Should().BeNull();
    }

    [Fact]
    public void TryExtractUrl_PrefersFirstUrl_InUriList()
    {
        const string uriList = "https://first/a.bin\nhttps://second/b.bin";

        DroppedLinkParser.TryExtractUrl(uriList).Should().Be("https://first/a.bin");
    }

    [Fact]
    public void RequestDownloadForUrl_RaisesDownloadUrlRequested()
    {
        MainWindowViewModel vm = BuildShell();
        string? requested = null;
        vm.DownloadUrlRequested += (_, url) => requested = url;

        vm.RequestDownloadForUrl("https://example.com/dropped.zip");

        requested.Should().Be("https://example.com/dropped.zip");
    }

    [Fact]
    public void RequestDownloadForUrl_IgnoresBlank()
    {
        MainWindowViewModel vm = BuildShell();
        bool raised = false;
        vm.DownloadUrlRequested += (_, _) => raised = true;

        vm.RequestDownloadForUrl("   ");

        raised.Should().BeFalse();
    }

    private static MainWindowViewModel BuildShell()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JustDownload.Core.Data.Models.Download>>(
                Array.Empty<JustDownload.Core.Data.Models.Download>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero));
        var downloads = new DownloadsListViewModel(
            repository, manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
            Substitute.For<IFileRevealer>(), Substitute.For<IFileCategorizer>(), clock);
        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        return new MainWindowViewModel(
            new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager), downloads,
            detail, new SidebarViewModel(downloads));
    }
}
