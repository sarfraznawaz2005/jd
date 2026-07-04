using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Unit tests for the New URL dialog view-model (TASK-053): URL paste auto-detects filename/category/folder,
/// the form enqueues a download (optionally starting it), and input is validated.
/// </summary>
public sealed class NewDownloadViewModelTests
{
    private sealed class Harness
    {
        public IResourceProbe Probe { get; } = Substitute.For<IResourceProbe>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();
        public IDownloadFolderProvider Folders { get; } = Substitute.For<IDownloadFolderProvider>();
        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IDownloadActions Actions { get; } = Substitute.For<IDownloadActions>();
        public IDuplicateDownloadCheck DuplicateCheck { get; } = Substitute.For<IDuplicateDownloadCheck>();

        public Harness()
        {
            Settings.Current.Returns(new AppSettings { ConnectionsPerDownload = 8 });
            DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(DuplicateCheckResult.None);
            Folders.GetBaseFolder().Returns(@"C:\Users\me\Downloads");
            Folders.GetFolderForCategory(Arg.Any<FileCategory>())
                .Returns(ci => $@"C:\Users\me\Downloads\{((FileCategory)ci[0])}");
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Program);
        }

        public NewDownloadViewModel Build() =>
            new(Probe, Categorizer, Folders, Settings, Manager, Actions, DuplicateCheck,
                NullLogger<NewDownloadViewModel>.Instance);

        public void SetProbe(string fileName, long? size, bool ranges) =>
            Probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ResourceProbeResult
                {
                    FinalUri = new Uri("https://download.mozilla.org/firefox-126.0.dmg"),
                    StatusCode = 200,
                    SupportsRanges = ranges,
                    TotalLength = size,
                    SuggestedFileName = fileName,
                }));
    }

    [Fact]
    public void StartsWithAutoCategory_AndBaseFolder()
    {
        var vm = new Harness().Build();
        vm.SelectedCategory.IsAuto.Should().BeTrue();
        vm.SaveToFolder.Should().Be(@"C:\Users\me\Downloads");
        vm.ConnectionsHint.Should().Contain("8 connections");
    }

    [Fact]
    public async Task DetectAsync_FillsFileName_Category_AndFolder()
    {
        var h = new Harness();
        h.SetProbe("firefox-126.0.dmg", 55_300_000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://download.mozilla.org/firefox-126.0.dmg";

        await vm.DetectAsync();

        vm.FileName.Should().Be("firefox-126.0.dmg");
        vm.SaveToFolder.Should().Be(@"C:\Users\me\Downloads\Program", "the folder follows the detected category");
        vm.DetectionMessage.Should().Contain("resumable");
        vm.CanSubmit.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAsync_DoesNotOverwriteUserEditedFields()
    {
        var h = new Harness();
        h.SetProbe("server-name.bin", 1000, ranges: true);
        var vm = h.Build();
        vm.FileName = "my-name.bin";    // user typed first
        vm.SaveToFolder = @"D:\Custom"; // user chose a folder
        vm.Url = "https://host.example/server-name.bin";

        await vm.DetectAsync();

        vm.FileName.Should().Be("my-name.bin", "a manual file name is not clobbered by detection");
        vm.SaveToFolder.Should().Be(@"D:\Custom", "a manual folder is not clobbered by detection");
    }

    [Fact]
    public async Task DetectAsync_BadResource_WarnsBeforeQueuing_AndDoesNotThrow()
    {
        // A server error status (404) means the link itself is bad — TASK-142 surfaces a prominent pre-queue
        // warning (UrlWarning), distinct from the soft "couldn't read" guidance used for transient failures.
        var h = new Harness();
        h.Probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResourceProbeResult>>(_ => throw new ResourceProbeException(new Uri("https://host.example/missing.bin"), 404));
        var vm = h.Build();
        vm.Url = "https://host.example/missing.bin";

        await vm.DetectAsync();

        vm.UrlWarning.Should().NotBeNull();
        vm.UrlWarning.Should().Contain("404");
        vm.DetectionMessage.Should().BeNull("a bad resource warns, it isn't a soft couldn't-read hint");
        vm.IsDetecting.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_BadResource_ThenEditingUrl_ClearsTheWarning()
    {
        var h = new Harness();
        h.Probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResourceProbeResult>>(_ => throw new ResourceProbeException(new Uri("https://host.example/missing.bin"), 410));
        var vm = h.Build();
        vm.Url = "https://host.example/missing.bin";
        await vm.DetectAsync();
        vm.UrlWarning.Should().NotBeNull();

        vm.Url = "https://host.example/other.bin"; // editing invalidates the previous verdict

        vm.UrlWarning.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_GoodResource_LeavesNoWarning()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.UrlWarning.Should().BeNull();
        vm.DetectionMessage.Should().NotBeNull();
        vm.DuplicateWarning.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_DuplicateOnDisk_WarnsWithSizeMatch()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: true));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.DuplicateWarning.Should().NotBeNull();
        vm.DuplicateWarning.Should().Contain("same file");
    }

    [Fact]
    public async Task DetectAsync_AlreadyInLibrary_Warns()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.AlreadyInLibrary));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.DuplicateWarning.Should().NotBeNull();
        vm.DuplicateWarning.Should().Contain("already queued");
    }

    [Fact]
    public async Task DetectAsync_DuplicateOnDisk_AutoRenamesToNextFreeName()
    {
        // Regression: re-downloading a file the user already had didn't offer a renamed copy — it just
        // warned and left the user to figure out a new name themselves (user-reported).
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), "file.bin", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: true));
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), "file (2).bin", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(DuplicateCheckResult.None);
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.FileName.Should().Be("file (2).bin");
        vm.DuplicateWarning.Should().Contain("file (2).bin");
    }

    [Fact]
    public async Task DetectAsync_DuplicateOnDisk_SkipsThroughTakenSuffixes_ToTheFirstFreeOne()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), "file.bin", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: true));
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), "file (2).bin", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: true));
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), "file (3).bin", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(DuplicateCheckResult.None);
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.FileName.Should().Be("file (3).bin");
    }

    [Fact]
    public async Task DetectAsync_DuplicateOnDisk_DoesNotAutoRename_WhenFileNameWasManuallyEdited()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: true));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        await vm.DetectAsync();
        vm.FileName = "my-custom-name.bin"; // the user's own choice pins it

        await vm.DetectAsync(); // e.g. re-checking after editing the URL/save folder

        vm.FileName.Should().Be("my-custom-name.bin", "a manually chosen name must not be silently replaced");
        vm.DuplicateWarning.Should().NotContain("renamed to");
    }

    [Fact]
    public async Task DuplicateWarning_ClearsWhenTheFileNameChanges()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, 1234, SizeMatches: false));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        await vm.DetectAsync();
        vm.DuplicateWarning.Should().NotBeNull();

        vm.FileName = "renamed.bin";

        vm.DuplicateWarning.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_UnexpectedProbeFailure_SurfacesMessage_AndDoesNotThrow()
    {
        // A failure type outside the previously-whitelisted set (e.g. a timeout) must still be caught and
        // surfaced, not escape as an unobserved exception that silently resets the form (TASK-120).
        var h = new Harness();
        h.Probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResourceProbeResult>>(_ => throw new TimeoutException("probe timed out"));
        var vm = h.Build();
        vm.Url = "https://host.example/slow.bin";

        await vm.DetectAsync();

        vm.DetectionMessage.Should().Contain("Couldn't read");
        vm.IsDetecting.Should().BeFalse();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("ftp://host/file", false)]
    [InlineData("https://host.example/file.zip", true)]
    public void Validation_AcceptsOnlyHttpUrls(string url, bool expectValidUrl)
    {
        var vm = new Harness().Build();
        vm.FileName = "file.zip";
        vm.Url = url;

        if (expectValidUrl)
        {
            vm.UrlError.Should().BeNull();
            vm.CanSubmit.Should().BeTrue();
        }
        else
        {
            vm.CanSubmit.Should().BeFalse();
        }
    }

    [Fact]
    public void Validation_RejectsInvalidFileNameCharacters()
    {
        var vm = new Harness().Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "bad:name?.zip";

        vm.FileNameError.Should().NotBeNull();
        vm.CanSubmit.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadNow_EnqueuesAndStarts()
    {
        var h = new Harness();
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(42L));
        var vm = h.Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "file.zip";
        vm.SaveToFolder = @"C:\Dest";

        bool closed = false;
        bool enqueuedSignal = false;
        vm.CloseRequested += (_, ok) => { closed = true; enqueuedSignal = ok; };

        vm.DownloadNowCommand.CanExecute(null).Should().BeTrue();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.FileName == "file.zip" &&
                r.DestinationDirectory == @"C:\Dest" &&
                r.MaxConnections == 8 &&
                r.Url == new Uri("https://host.example/file.zip")),
            Arg.Any<CancellationToken>());
        h.Actions.Received(1).Start(42);
        closed.Should().BeTrue();
        enqueuedSignal.Should().BeTrue();
    }

    [Fact]
    public async Task SetAuthContext_FlowsReferrerAndCookies_IntoEnqueue()
    {
        // A browser hand-off (TASK-091) carries the captured referrer/cookies into the enqueue so an
        // authenticated download succeeds; cookies are handed to the engine (which keychains them).
        var h = new Harness();
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(11L));
        var vm = h.Build();
        vm.Url = "https://host.example/clip.mp4";
        vm.FileName = "clip.mp4";
        vm.SaveToFolder = @"C:\Dest";
        vm.SetAuthContext("https://host.example/watch", "session=abc123");

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.Referrer == "https://host.example/watch" && r.Cookies == "session=abc123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPaused_EnqueuesWithoutStarting()
    {
        var h = new Harness();
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(7L));
        var vm = h.Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "file.zip";
        vm.SaveToFolder = @"C:\Dest";
        vm.UseSegmentation = false; // single connection

        await vm.AddPausedCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.MaxConnections == 1), Arg.Any<CancellationToken>());
        h.Actions.DidNotReceive().Start(Arg.Any<long>());
    }

    [Fact]
    public async Task ProxyOverride_On_FlowsConfigIntoEnqueue()
    {
        var h = new Harness();
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5L));
        var vm = h.Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "file.zip";
        vm.SaveToFolder = @"C:\Dest";
        vm.UseProxyOverride = true;
        vm.OverrideProxyKind = JustDownload.Core.Transport.Proxy.ProxyKind.Socks5;
        vm.OverrideProxyHost = "proxy.local";
        vm.OverrideProxyPort = 1080;
        vm.OverrideProxyUsername = "user";
        vm.OverrideProxyPassword = "pw";

        vm.CanSubmit.Should().BeTrue();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.Proxy != null
                && r.Proxy.Kind == JustDownload.Core.Transport.Proxy.ProxyKind.Socks5
                && r.Proxy.Host == "proxy.local"
                && r.Proxy.Port == 1080
                && r.Proxy.Credentials != null
                && r.Proxy.Credentials.Username == "user"
                && r.Proxy.Credentials.Password == "pw"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProxyOverride_Off_LeavesProxyNull()
    {
        var h = new Harness();
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(6L));
        var vm = h.Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "file.zip";
        vm.SaveToFolder = @"C:\Dest";

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.Proxy == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProxyOverride_MissingHostOrBadPort_BlocksSubmit()
    {
        var h = new Harness();
        var vm = h.Build();
        vm.Url = "https://host.example/file.zip";
        vm.FileName = "file.zip";
        vm.SaveToFolder = @"C:\Dest";
        vm.UseProxyOverride = true; // host blank, port 0

        vm.ProxyOverrideError.Should().NotBeNull();
        vm.CanSubmit.Should().BeFalse();

        vm.OverrideProxyHost = "proxy.local";
        vm.OverrideProxyPort = 70000; // out of range
        vm.ProxyOverrideError.Should().NotBeNull();
        vm.CanSubmit.Should().BeFalse();

        vm.OverrideProxyPort = 1080;
        vm.ProxyOverrideError.Should().BeNull();
        vm.CanSubmit.Should().BeTrue();
    }

    [Fact]
    public void Cancel_ClosesWithoutEnqueue()
    {
        var h = new Harness();
        var vm = h.Build();
        bool? result = null;
        vm.CloseRequested += (_, ok) => result = ok;

        vm.CancelCommand.Execute(null);

        result.Should().BeFalse();
        h.Manager.DidNotReceive().EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitCategory_RetargetsFolder()
    {
        var h = new Harness();
        var vm = h.Build();

        vm.SelectedCategory = vm.Categories.Single(c => c.Category == FileCategory.Video);

        vm.SaveToFolder.Should().Be(@"C:\Users\me\Downloads\Video");
        await Task.CompletedTask;
    }
}
