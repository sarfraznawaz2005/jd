using FluentAssertions;
using JustDownload.App.Formatting;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The New Download dialog must send the same sign-in context when it reads a link as when it downloads
/// it (TASK-263). Detection used to probe with no headers at all, so a link behind a login was read as
/// the login page — wrong size, wrong file name — or hung against a CGI that never answers an
/// unauthenticated caller. The dialog also accepts a pasted <c>Cookie</c>/referrer for a URL typed by
/// hand, which has no browser session behind it.
/// </summary>
public sealed class NewDownloadAuthContextTests
{
    private sealed class Harness
    {
        public IResourceProbe Probe { get; } = Substitute.For<IResourceProbe>();
        public IMediaExtractorRegistry MediaRegistry { get; } = Substitute.For<IMediaExtractorRegistry>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();
        public IDownloadFolderProvider Folders { get; } = Substitute.For<IDownloadFolderProvider>();
        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IDownloadActions Actions { get; } = Substitute.For<IDownloadActions>();
        public IDuplicateDownloadCheck DuplicateCheck { get; } = Substitute.For<IDuplicateDownloadCheck>();
        public ISecretStore Secrets { get; } = Substitute.For<ISecretStore>();
        public ITosNoticeGate TosGate { get; } = Substitute.For<ITosNoticeGate>();
        public IProcessLauncher Launcher { get; } = Substitute.For<IProcessLauncher>();

        public Harness()
        {
            TosGate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
            Settings.Current.Returns(new AppSettings { ConnectionsPerDownload = 8 });
            DuplicateCheck
                .CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(DuplicateCheckResult.None);
            Folders.GetBaseFolder().Returns(@"C:\Users\me\Downloads");
            Folders.GetFolderForCategory(Arg.Any<FileCategory>())
                .Returns(ci => $@"C:\Users\me\Downloads\{((FileCategory)ci[0])}");
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Compressed);
            MediaRegistry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(MediaExtractionResult.None));

            // Captured in the Returns callback rather than via Received()/Arg.Do, so the assertion reads
            // the arguments of the call the view-model actually made.
            Probe.ProbeAsync(
                    Arg.Any<Uri>(),
                    Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    LastProbeHeaders = (IReadOnlyList<KeyValuePair<string, string>>?)ci[1];
                    return Task.FromResult(new ResourceProbeResult
                    {
                        FinalUri = new Uri("http://router.local/config.bin"),
                        StatusCode = 200,
                        SupportsRanges = false,
                        TotalLength = 4096,
                        SuggestedFileName = "config.bin",
                    });
                });

            Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    LastEnqueue = (EnqueueDownloadRequest)ci[0];
                    return Task.FromResult(1L);
                });
        }

        /// <summary>The headers the most recent detection probe was called with.</summary>
        public IReadOnlyList<KeyValuePair<string, string>>? LastProbeHeaders { get; private set; }

        /// <summary>The request the most recent submit enqueued.</summary>
        public EnqueueDownloadRequest? LastEnqueue { get; private set; }

        public NewDownloadViewModel Build() =>
            new(Probe, MediaRegistry, Categorizer, Folders, Settings, Manager, Actions, DuplicateCheck, Secrets,
                TosGate, NullLogger<NewDownloadViewModel>.Instance, Launcher, TimeSpan.FromSeconds(5));

    }

    private const string Url = "http://router.local/cgi/backup.cgi";

    [Fact]
    public async Task Detection_SendsTheCapturedBrowserSession()
    {
        // The regression: the extension captured cookies, but the probe was called with headers: null.
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.SetAuthContext("http://router.local/admin", "sid=abc123");
        vm.Url = Url;

        await vm.DetectAsync();

        h.LastProbeHeaders.Should().Contain(new KeyValuePair<string, string>("Cookie", "sid=abc123"))
            .And.Contain(new KeyValuePair<string, string>("Referer", "http://router.local/admin"));
    }

    [Fact]
    public async Task Detection_SendsNoAuthHeaders_WhenThereIsNoSession()
    {
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.Url = Url;

        await vm.DetectAsync();

        h.LastProbeHeaders.Should().BeEmpty();
    }

    [Fact]
    public async Task Detection_SendsThePastedCookie_ForAHandTypedUrl()
    {
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.Url = Url;
        vm.UseSignIn = true;
        vm.SignInCookies = "  session=pasted  ";

        await vm.DetectAsync();

        h.LastProbeHeaders.Should().Contain(new KeyValuePair<string, string>("Cookie", "session=pasted"));
    }

    [Fact]
    public async Task PastedCookie_OverridesTheCapturedOne()
    {
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.SetAuthContext(null, "sid=stale");
        vm.Url = Url;
        vm.UseSignIn = true;
        vm.SignInCookies = "sid=fresh";

        await vm.DetectAsync();

        h.LastProbeHeaders.Should().Contain(new KeyValuePair<string, string>("Cookie", "sid=fresh"));
    }

    [Fact]
    public async Task UncheckedSignIn_KeepsTheCapturedSession_AndIgnoresTheTypedOne()
    {
        // Leaving the box unchecked must not throw away what the browser already gave us.
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.SetAuthContext(null, "sid=captured");
        vm.Url = Url;
        vm.SignInCookies = "sid=ignored";
        vm.UseSignIn = false;

        await vm.DetectAsync();

        h.LastProbeHeaders.Should().Contain(new KeyValuePair<string, string>("Cookie", "sid=captured"));
    }

    [Fact]
    public async Task Submit_EnqueuesTheSameSignInContextDetectionUsed()
    {
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.Url = Url;
        vm.FileName = "backup.bin";
        vm.UseSignIn = true;
        vm.SignInCookies = "session=pasted";
        vm.SignInReferrer = "http://router.local/admin";

        await vm.DownloadNowCommand.ExecuteAsync(null);

        EnqueueDownloadRequest? request = h.LastEnqueue;
        request.Should().NotBeNull();
        request!.Cookies.Should().Be("session=pasted");
        request.Referrer.Should().Be("http://router.local/admin");
    }

    [Fact]
    public async Task SkipLinkCheck_MakesNoRequestAtAll()
    {
        // A one-shot URL only answers once: the pre-check must not be the thing that spends that answer.
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.SkipLinkCheck = true;
        vm.Url = Url;

        vm.CanDetect.Should().BeFalse("the view's auto-detect trigger is gated on this");
        await vm.DetectAsync();

        await h.Probe.DidNotReceive().ProbeAsync(
            Arg.Any<Uri>(),
            Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(),
            Arg.Any<CancellationToken>());
        await h.Probe.DidNotReceive().OpenAsync(
            Arg.Any<Uri>(),
            Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipLinkCheck_StillEnqueuesTheDownload()
    {
        // Skipping the pre-check costs the auto-filled metadata, not the download itself.
        var h = new Harness();
        NewDownloadViewModel vm = h.Build();
        vm.SkipLinkCheck = true;
        vm.Url = Url;
        vm.FileName = "backup.zip";

        await vm.DownloadNowCommand.ExecuteAsync(null);

        h.LastEnqueue.Should().NotBeNull();
        h.LastEnqueue!.FileName.Should().Be("backup.zip");
        h.LastEnqueue.TotalBytes.Should().BeNull("nothing read the link, so the size is genuinely unknown");
    }

    [Fact]
    public void SkipLinkCheck_IsOffByDefault()
    {
        new Harness().Build().SkipLinkCheck.Should().BeFalse();
    }

    [Fact]
    public void HasCapturedSession_TracksWhatTheExtensionHandedOver()
    {
        // Drives the dialog's "cookies already captured" note; the value itself is never bound (§3).
        NewDownloadViewModel vm = new Harness().Build();
        vm.HasCapturedSession.Should().BeFalse();

        vm.SetAuthContext(null, "sid=abc");

        vm.HasCapturedSession.Should().BeTrue();
    }
}
