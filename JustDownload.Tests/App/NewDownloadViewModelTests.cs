using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.App.Formatting;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Proxy;
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
        public IMediaExtractorRegistry MediaRegistry { get; } = Substitute.For<IMediaExtractorRegistry>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();
        public IDownloadFolderProvider Folders { get; } = Substitute.For<IDownloadFolderProvider>();
        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IDownloadActions Actions { get; } = Substitute.For<IDownloadActions>();
        public IDuplicateDownloadCheck DuplicateCheck { get; } = Substitute.For<IDuplicateDownloadCheck>();
        public ISecretStore Secrets { get; } = Substitute.For<ISecretStore>();
        public ITosNoticeGate TosGate { get; } = Substitute.For<ITosNoticeGate>();

        public Harness()
        {
            // The real gate is a no-op returning true once the notice has been suppressed (TosNoticeGate);
            // accepting by default keeps every pre-existing test exercising the enqueue path.
            TosGate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

            Settings.Current.Returns(new AppSettings { ConnectionsPerDownload = 8 });
            DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(DuplicateCheckResult.None);
            Folders.GetBaseFolder().Returns(@"C:\Users\me\Downloads");
            Folders.GetFolderForCategory(Arg.Any<FileCategory>())
                .Returns(ci => $@"C:\Users\me\Downloads\{((FileCategory)ci[0])}");
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Program);

            // The registry never returns null now — an unconfigured call still hands back an empty result.
            MediaRegistry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(MediaExtractionResult.None));
        }

        public NewDownloadViewModel Build(TimeSpan? detectTimeout = null) =>
            new(Probe, MediaRegistry, Categorizer, Folders, Settings, Manager, Actions, DuplicateCheck, Secrets,
                TosGate, NullLogger<NewDownloadViewModel>.Instance, detectTimeout);

        /// <summary>Makes the media-extractor registry recognise <paramref name="url"/> as the given HLS/DASH
        /// <paramref name="source"/> (TASK-241) — mirrors <c>MediaVariantPickerTests.RegistryReturning</c>.</summary>
        public void SetMediaSource(string url, MediaSource source) =>
            MediaRegistry.ExtractAsync(
                    Arg.Is<MediaRequest>(r => r.Url == new Uri(url)), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaExtractionResult
                {
                    Source = source,
                    Attempts = [MediaExtractionAttempt.Accepted(source.ExtractorName)],
                }));

        /// <summary>Makes the registry find nothing for every URL, reporting <paramref name="attempts"/> as the
        /// per-extractor outcomes that explain why.</summary>
        public void SetMediaExtractionFailure(params MediaExtractionAttempt[] attempts) =>
            MediaRegistry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new MediaExtractionResult { Attempts = attempts }));

        /// <summary>Makes the registry resolve <paramref name="source"/> on its first call and then hang until
        /// cancelled on every later one — the shape of a re-detect that is still in flight when the user
        /// submits (TASK-247).</summary>
        public void SetMediaSourceThenHang(MediaSource source)
        {
            int calls = 0;
            MediaRegistry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        return Task.FromResult(new MediaExtractionResult
                        {
                            Source = source,
                            Attempts = [MediaExtractionAttempt.Accepted(source.ExtractorName)],
                        });
                    }

                    var tcs = new TaskCompletionSource<MediaExtractionResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    ((CancellationToken)ci[1]).Register(() => tcs.TrySetCanceled());
                    return tcs.Task;
                });
        }

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

        /// <summary>Makes the probe hang until its <see cref="CancellationToken"/> is cancelled, mimicking a
        /// server that accepts the connection but never responds — the scenario <see cref="DetectTimeout"/>
        /// (or a newer detect call) has to unblock.</summary>
        public void SetProbeHangsUntilCancelled() =>
            Probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    // RunContinuationsAsynchronously matters: a real HTTP request's cancellation never resumes
                    // its awaiter inline on the thread that called Cancel() (the BCL's socket-bound tasks post
                    // continuations, they don't reenter). Without this, TrySetCanceled() below would resume
                    // DetectAsync's awaiter synchronously and reentrantly, mid-way through a *second* call's
                    // own _detectCts?.Cancel() — a test-only artifact that doesn't reflect real timing and
                    // would make the "superseded" test spuriously see its own not-yet-reassigned cts.
                    var tcs = new TaskCompletionSource<ResourceProbeResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    ((CancellationToken)ci[2]).Register(() => tcs.TrySetCanceled());
                    return tcs.Task;
                });
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

    // ---- HLS/DASH manifest routing (TASK-241 bug fix) ----

    [Fact]
    public async Task DetectAsync_HlsMasterPlaylistUrl_DetectsAsHls_AndEnqueuesTheChosenVariant_NotTheRawManifest()
    {
        // Repro (user-reported): an extension-sniffed Twitter/X .m3u8 master playlist submitted via the New
        // Download dialog used to be probed like a plain file — the raw "#EXTM3U ... #EXT-X-STREAM-INF" text
        // itself got saved as the "video". It must instead be recognised as HLS and routed through the real
        // extractor path, enqueuing the resolved variant stream with MediaKind.Hls so DownloadManager takes
        // the segments->concat path instead of a plain HTTP GET of the manifest.
        var h = new Harness();
        const string masterUrl = "https://video.twimg.com/ext_tw_video/master.m3u8";
        var source = new MediaSource
        {
            ExtractorName = "hls",
            Kind = MediaKind.Hls,
            Url = new Uri(masterUrl),
            SuggestedFileName = "master",
            Variants =
            [
                new VideoVariant("https://video.twimg.com/ext_tw_video/480.m3u8", 480, 800_000),
                new VideoVariant("https://video.twimg.com/ext_tw_video/1080.m3u8", 1080, 3_000_000),
            ],
        };
        h.SetMediaSource(masterUrl, source);
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(99L));
        var vm = h.Build();
        vm.Url = masterUrl;

        await vm.DetectAsync();

        // AppSettings.DefaultVideoQuality defaults to 1080p (JustDownload.Core.Settings.AppSettings), so the
        // 1080p variant is pre-selected exactly like the quality picker's own default selection.
        vm.FileName.Should().Be("master.mkv", "the default container is MKV (AppSettings.DefaultContainer)");
        vm.SaveToFolder.Should().Be(@"C:\Users\me\Downloads\Video");
        vm.CanSubmit.Should().BeTrue();

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == MediaKind.Hls &&
                r.Url == new Uri("https://video.twimg.com/ext_tw_video/1080.m3u8")),
            Arg.Any<CancellationToken>());
        await h.Probe.DidNotReceive().ProbeAsync(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_PlainProgressiveUrl_NeverConsultsMediaRegistry_AndBehavesExactlyAsBefore()
    {
        // AC2 regression: a URL that isn't recognisable as an HLS/DASH manifest by extension must never reach
        // the media-extractor registry at all — the plain IResourceProbe path (and MediaKind == null on
        // enqueue) is entirely unaffected by TASK-241's fix.
        var h = new Harness();
        h.SetProbe("firefox-126.0.dmg", 55_300_000, ranges: true);
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1L));
        var vm = h.Build();
        vm.Url = "https://download.mozilla.org/firefox-126.0.dmg";

        await vm.DetectAsync();

        vm.FileName.Should().Be("firefox-126.0.dmg");
        vm.DetectionMessage.Should().Contain("resumable");
        await h.MediaRegistry.DidNotReceive().ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == null &&
                r.Url == new Uri("https://download.mozilla.org/firefox-126.0.dmg")),
            Arg.Any<CancellationToken>());
    }

    // ---- Video-host page URL routing through the registry (TASK-265) ----
    // A pasted YouTube / X / Facebook / Instagram *page* URL used to short-circuit straight to the plain
    // probe path, so the per-extractor decline/failure reasons were never seen for them. Now the
    // MediaHosts allowlist lets such URLs reach the registry too.

    [Fact]
    public async Task DetectAsync_YouTubeWatchUrl_ConsultsMediaRegistry()
    {
        var h = new Harness();
        h.SetProbe("video.mp4", 1000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

        await vm.DetectAsync();

        await h.MediaRegistry.Received(1).ExtractAsync(
            Arg.Is<MediaRequest>(r => r.Url == new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_XStatusUrl_ConsultsMediaRegistry()
    {
        var h = new Harness();
        h.SetProbe("video.mp4", 1000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://x.com/SomeUser/status/1234567890";

        await vm.DetectAsync();

        await h.MediaRegistry.Received(1).ExtractAsync(
            Arg.Is<MediaRequest>(r => r.Url == new Uri("https://x.com/SomeUser/status/1234567890")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_FacebookReelUrl_ConsultsMediaRegistry()
    {
        var h = new Harness();
        h.SetProbe("video.mp4", 1000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://www.facebook.com/reel/9876543210";

        await vm.DetectAsync();

        await h.MediaRegistry.Received(1).ExtractAsync(
            Arg.Is<MediaRequest>(r => r.Url == new Uri("https://www.facebook.com/reel/9876543210")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_PlainNonVideoHostUrl_NeverConsultsMediaRegistry()
    {
        // AC2 regression for the new gate: a plain file URL on a host outside the allowlist must still
        // never reach the registry — only a pasted video-host page URL now does.
        var h = new Harness();
        h.SetProbe("firefox-126.0.dmg", 55_300_000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://example.com/file.zip";

        await vm.DetectAsync();

        vm.FileName.Should().Be("firefox-126.0.dmg");
        await h.MediaRegistry.DidNotReceive().ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_VideoHostUrl_ExtractorFailure_SurfacesExplanationNotGenericNotFound()
    {
        // The whole point: a failed attempt (yt-dlp disabled, no in-house match) on a video-host page URL
        // now reaches the explanation path, so UrlWarning carries the attributed reason instead of the
        // generic "couldn't find" string.
        var h = new Harness();
        h.SetMediaExtractionFailure(
            MediaExtractionAttempt.Failed("yt-dlp", "Sign in to confirm you're not a bot"));
        h.SetProbe("video.mp4", 1000, ranges: true);
        var vm = h.Build();
        vm.Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

        await vm.DetectAsync();

        vm.UrlWarning.Should().NotBeNull("a failure reached through the registry is explained, not swallowed");
        vm.UrlWarning.Should().Contain("yt-dlp").And.Contain("bot",
            "the att's own reason must be what the user sees");
        vm.UrlWarning.Should().NotBe(MediaExtractionMessage.NoMediaFound,
            "the generic 'no media found' must not replace the real diagnosis");
    }

    [Fact]
    public async Task Submit_WhileASupersedingDetectIsInFlight_StillEnqueuesTheDetectedMedia()
    {
        // Repro (user-reported, DB rows 60/62): the dialog showed "Adaptive stream detected" and a .mp4 file
        // name, yet the enqueued row had media_kind NULL, the master-playlist URL, and 334 bytes — the raw
        // playlist text saved as the "video". Clicking "Download now" moves focus off the URL box, and
        // NewDownloadWindow.OnUrlCommitted fires a fresh DetectAsync on that blur *before* the button's
        // command runs. That pass wiped the already-resolved media source and was still awaiting the
        // extractor when SubmitAsync read it, so the request silently degraded to a plain progressive
        // download of the manifest.
        var h = new Harness();
        const string masterUrl =
            "https://video.twimg.com/amplify_video/2087460753578098688/pl/pM5J6caWDUGNnNNT.m3u8?tag=29";
        const string variantUrl = "https://video.twimg.com/amplify_video/pl/avc1/1584x1080/WApxn0.m3u8";
        var source = new MediaSource
        {
            ExtractorName = "hls",
            Kind = MediaKind.Hls,
            Url = new Uri(masterUrl),
            SuggestedFileName = "pM5J6caWDUGNnNNT",
            Variants = [new VideoVariant(variantUrl, 1080, 3_000_000)],
        };
        h.SetMediaSourceThenHang(source);
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(62L));
        var vm = h.Build();
        vm.Url = masterUrl;

        await vm.DetectAsync();
        vm.DetectionMessage.Should().Contain("Adaptive stream detected");

        // The blur-triggered second pass: started, state-clearing already done, still awaiting the extractor.
        Task inFlight = vm.DetectAsync();
        vm.IsDetecting.Should().BeTrue("the second pass must still be running when the user submits");

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == MediaKind.Hls &&
                r.Url == new Uri(variantUrl)),
            Arg.Any<CancellationToken>());

        vm.Dispose();
        await inFlight;
    }

    [Fact]
    public async Task Submit_WhileASupersedingDetectIsInFlight_StillEnqueuesAMediaPlaylistWithNoVariants()
    {
        // DB row 63: the same failure for a *media* (non-master) playlist, which resolves to no variants at
        // all — the download URL is then the playlist itself, but it still must carry MediaKind.Hls so the
        // engine takes the segments->concat path rather than a raw GET.
        var h = new Harness();
        const string mediaUrl =
            "https://video.twimg.com/amplify_video/pl/avc1/1584x1080/WApxn0wcZ0NSP6FI.m3u8";
        var source = new MediaSource
        {
            ExtractorName = "hls",
            Kind = MediaKind.Hls,
            Url = new Uri(mediaUrl),
            SuggestedFileName = "WApxn0wcZ0NSP6FI",
        };
        h.SetMediaSourceThenHang(source);
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(63L));
        var vm = h.Build();
        vm.Url = mediaUrl;

        await vm.DetectAsync();
        Task inFlight = vm.DetectAsync();

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.MediaKind == MediaKind.Hls && r.Url == new Uri(mediaUrl)),
            Arg.Any<CancellationToken>());

        vm.Dispose();
        await inFlight;
    }

    [Fact]
    public async Task Submit_ManifestUrlThatNoExtractorCanResolve_IsRefused_NotEnqueuedAsARawFile()
    {
        // No silent failures (§5): if the URL is a playlist/manifest but no media source could be resolved,
        // enqueuing it as a plain download would save the playlist text as the "video". Refuse with a clear
        // warning instead.
        var h = new Harness();
        const string manifestUrl = "https://video.twimg.com/amplify_video/pl/pM5J6caWDUGNnNNT.m3u8";
        h.SetMediaExtractionFailure(MediaExtractionAttempt.Declined("hls"));
        h.SetProbe("pM5J6caWDUGNnNNT.m3u8", 334, ranges: true);
        var vm = h.Build();
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        vm.Url = manifestUrl;

        await vm.DetectAsync();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.DidNotReceive().EnqueueAsync(
            Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
        vm.UrlWarning.Should().Contain("playlist");
        closed.Should().BeNull("the dialog must stay open so the user sees why nothing was queued");
    }

    [Fact]
    public async Task Submit_DetectedMedia_TosNoticeDeclined_EnqueuesNothing_AndKeepsTheDialogOpen()
    {
        // CLAUDE.md §5: a media download must show the one-time "may violate site ToS" notice. Declining it
        // cancels the download — nothing is queued and the dialog stays open with a reason.
        var h = new Harness();
        h.TosGate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        const string masterUrl = "https://video.twimg.com/ext_tw_video/master.m3u8";
        h.SetMediaSource(masterUrl, HlsSource(masterUrl));
        var vm = h.Build();
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        vm.Url = masterUrl;

        await vm.DetectAsync();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.TosGate.Received(1).ConfirmAsync(Arg.Any<CancellationToken>());
        await h.Manager.DidNotReceive().EnqueueAsync(
            Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
        h.Actions.DidNotReceive().Start(Arg.Any<long>());
        vm.UrlWarning.Should().Contain("cancelled");
        closed.Should().BeNull("the dialog must stay open so the user sees why nothing was queued");
    }

    [Fact]
    public async Task Submit_DetectedMedia_TosNoticeAccepted_EnqueuesTheMediaRequestUnchanged()
    {
        var h = new Harness();
        h.TosGate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        const string masterUrl = "https://video.twimg.com/ext_tw_video/master.m3u8";
        h.SetMediaSource(masterUrl, HlsSource(masterUrl));
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(77L));
        var vm = h.Build();
        vm.Url = masterUrl;

        await vm.DetectAsync();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.TosGate.Received(1).ConfirmAsync(Arg.Any<CancellationToken>());
        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == MediaKind.Hls &&
                r.Url == new Uri("https://video.twimg.com/ext_tw_video/1080.m3u8") &&
                r.MediaAudioUrl == null &&
                r.MediaContainer == MediaContainer.Mkv),
            Arg.Any<CancellationToken>());
        h.Actions.Received(1).Start(77L);
    }

    [Fact]
    public async Task Submit_PlainProgressiveUrl_NeverConsultsTheTosGate()
    {
        // The notice is for media downloads only (CLAUDE.md §5) — an ordinary file must never raise it.
        var h = new Harness();
        h.SetProbe("firefox-126.0.dmg", 55_300_000, ranges: true);
        h.Manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1L));
        var vm = h.Build();
        vm.Url = "https://download.mozilla.org/firefox-126.0.dmg";

        await vm.DetectAsync();
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.TosGate.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.MediaKind == null), Arg.Any<CancellationToken>());
    }

    /// <summary>An HLS master playlist with 480p/1080p variants — the shape the dialog's default selection
    /// resolves to the 1080p stream (AppSettings.DefaultVideoQuality).</summary>
    private static MediaSource HlsSource(string masterUrl) => new()
    {
        ExtractorName = "hls",
        Kind = MediaKind.Hls,
        Url = new Uri(masterUrl),
        SuggestedFileName = "master",
        Variants =
        [
            new VideoVariant("https://video.twimg.com/ext_tw_video/480.m3u8", 480, 800_000),
            new VideoVariant("https://video.twimg.com/ext_tw_video/1080.m3u8", 1080, 3_000_000),
        ],
    };

    [Fact]
    public async Task DetectAsync_ManifestUrl_NetworkFailure_WarnsAboutConnectivity_NotMissingMedia()
    {
        var h = new Harness();
        h.SetMediaExtractionFailure(
            MediaExtractionAttempt.NetworkFailure("hls", "HttpRequestException: No such host is known."));
        h.SetProbe("x.m3u8", 334, ranges: true);
        var vm = h.Build();
        vm.Url = "https://video.twimg.com/amplify_video/pl/x.m3u8";

        await vm.DetectAsync();

        vm.UrlWarning.Should().Contain("Network error").And.Contain("video.twimg.com");
    }

    [Fact]
    public async Task DetectAsync_ManifestUrl_ExtractorReason_IsSurfaced_AndRepeatedWhenSubmitRefuses()
    {
        var h = new Harness();
        h.SetMediaExtractionFailure(MediaExtractionAttempt.Failed("yt-dlp", "HTTP Error 429: Too Many Requests"));
        h.SetProbe("x.m3u8", 334, ranges: true);
        var vm = h.Build();
        vm.Url = "https://video.twimg.com/amplify_video/pl/x.m3u8";

        await vm.DetectAsync();
        vm.UrlWarning.Should().Contain("yt-dlp: HTTP Error 429: Too Many Requests");

        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.DidNotReceive().EnqueueAsync(
            Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
        vm.UrlWarning.Should().Contain("playlist").And.Contain("HTTP Error 429",
            "the refusal keeps the reason the user needs, not just 'couldn't be read'");
    }

    [Fact]
    public async Task DetectAsync_ManifestUrl_AllExtractorsDeclined_AddsNoExtraWarning()
    {
        var h = new Harness();
        h.SetMediaExtractionFailure(MediaExtractionAttempt.Declined("hls"), MediaExtractionAttempt.Declined("dash"));
        h.SetProbe("x.m3u8", 334, ranges: true);
        var vm = h.Build();
        vm.Url = "https://video.twimg.com/amplify_video/pl/x.m3u8";

        await vm.DetectAsync();

        vm.UrlWarning.Should().BeNull("a plain decline is not a failure worth warning about on its own");
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
        vm.DuplicateWarningIsInformational.Should().BeFalse("a real collision reads as a warning, not an FYI");
    }

    [Fact]
    public async Task DetectAsync_RecoverableInLibrary_ShowsDistinctInformationalMessage_AndDoesNotAutoRename()
    {
        // TASK-230: nothing is claiming the destination (the earlier attempt is Failed/Expired), so renaming
        // around it would just hide that a previous attempt exists rather than surfacing it.
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.RecoverableInLibrary, ExistingDownloadId: 7));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";

        await vm.DetectAsync();

        vm.FileName.Should().Be("file.bin", "unlike a real collision, this must not auto-rename");
        vm.DuplicateWarning.Should().NotBeNull();
        vm.DuplicateWarning.Should().Contain("didn't finish");
        vm.DuplicateWarning.Should().NotContain("Rename it, or cancel to skip", "it's a heads-up, not a collision warning");
        vm.DuplicateWarningIsInformational.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateWarningIsInformational_ClearsAlongsideTheWarning()
    {
        var h = new Harness();
        h.SetProbe("file.bin", 1234, ranges: true);
        h.DuplicateCheck.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateCheckResult(DuplicateKind.RecoverableInLibrary, ExistingDownloadId: 7));
        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        await vm.DetectAsync();
        vm.DuplicateWarningIsInformational.Should().BeTrue(); // sanity

        vm.FileName = "something-else.bin"; // a manual edit invalidates the stale warning

        vm.DuplicateWarning.Should().BeNull();
        vm.DuplicateWarningIsInformational.Should().BeFalse();
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

    [Fact]
    public async Task DetectAsync_ServerNeverResponds_TimesOutRatherThanHangingForever()
    {
        // A server that accepts the connection but never sends a response has no built-in timeout to save it
        // (HttpClient.Timeout is deliberately infinite for large transfers) — without DetectAsync's own
        // CancelAfter, "Detecting…" would spin until the dialog is closed.
        var h = new Harness();
        h.SetProbeHangsUntilCancelled();
        var vm = h.Build(detectTimeout: TimeSpan.FromMilliseconds(50));
        vm.Url = "https://host.example/never-responds.bin";

        await vm.DetectAsync();

        vm.IsDetecting.Should().BeFalse();
        vm.DetectionMessage.Should().Contain("Couldn't read");
        vm.DetectionMessageIsError.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAsync_SupersededByNewerCall_StaysSilent_UnlikeATimeout()
    {
        // A newer DetectAsync call cancelling an older in-flight one is normal (re-typing the URL) and must
        // not show an error — only DetectAsync's own CancelAfter firing on an otherwise-unsuperseded call
        // should.
        var h = new Harness();
        h.SetProbeHangsUntilCancelled();
        var vm = h.Build(detectTimeout: TimeSpan.FromSeconds(30)); // long enough that only supersession fires
        vm.Url = "https://host.example/first.bin";

        Task first = vm.DetectAsync();
        h.SetProbe("second.bin", 1000, ranges: true);
        vm.Url = "https://host.example/second.bin";
        await vm.DetectAsync();
        await first;

        vm.DetectionMessageIsError.Should().BeFalse("the superseded call must not report a spurious timeout");
        vm.FileName.Should().Be("second.bin", "the newer call's result is what the dialog shows");
    }

    [Fact]
    public async Task Dispose_CancelsAnInFlightProbe_InsteadOfLeavingItRunning()
    {
        var h = new Harness();
        h.SetProbeHangsUntilCancelled();
        var vm = h.Build(detectTimeout: TimeSpan.FromSeconds(30));
        vm.Url = "https://host.example/never-responds.bin";
        Task detecting = vm.DetectAsync();

        vm.Dispose();

        // Bounded await: if Dispose did not cancel the probe this would hang until the 30s DetectTimeout, so a
        // short wait is itself part of the assertion (a hang here is a test failure, not a fluke).
        Task completed = await Task.WhenAny(detecting, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().BeSameAs(detecting, "Dispose should cancel the in-flight probe promptly");
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

    // ---- Remembered choices across runs (TASK-227) ----

    /// <summary>Captures the settings mutation a submit performs, so the next dialog can be built from it.</summary>
    private static async Task<AppSettings> SubmitAndCaptureAsync(Harness h, Action<NewDownloadViewModel> arrange)
    {
        var saved = new AppSettings { ConnectionsPerDownload = 8 };
        h.Settings.UpdateAsync(Arg.Do<Func<AppSettings, AppSettings>>(update => saved = update(saved)))
            .Returns(_ => Task.FromResult(saved));

        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        vm.FileName = "file.bin";
        arrange(vm);

        await vm.DownloadNowCommand.ExecuteAsync(null);
        return saved;
    }

    [Fact]
    public async Task Submit_RemembersOptionChoices_ForTheNextRun()
    {
        var h = new Harness();

        AppSettings saved = await SubmitAndCaptureAsync(h, vm =>
        {
            vm.SaveToFolder = @"D:\Chosen";
            vm.SelectedCategory = vm.Categories.Single(c => c.Category == FileCategory.Video);
            vm.UseSegmentation = false;
            vm.UseAlternateUrls = true;
        });

        saved.NewDownloadFolder.Should().Be(@"D:\Chosen");
        saved.NewDownloadCategory.Should().Be("Video");
        saved.NewDownloadUseSegmentation.Should().BeFalse();
        saved.NewDownloadUseAlternateUrls.Should().BeTrue();

        // The next dialog opens on exactly those choices.
        h.Settings.Current.Returns(saved);
        var next = h.Build();
        next.SaveToFolder.Should().Be(@"D:\Chosen");
        next.SelectedCategory.Category.Should().Be(FileCategory.Video);
        next.UseSegmentation.Should().BeFalse();
        next.UseAlternateUrls.Should().BeTrue();
    }

    [Fact]
    public async Task Submit_RemembersProxyOverride_WithThePasswordInTheKeychain()
    {
        var h = new Harness();
        h.Secrets.StoreAsync("hunter2", Arg.Any<CancellationToken>()).Returns(Task.FromResult("ref-1"));

        AppSettings saved = await SubmitAndCaptureAsync(h, vm =>
        {
            vm.UseProxyOverride = true;
            vm.OverrideProxyKind = ProxyKind.Socks5;
            vm.OverrideProxyHost = "proxy.example";
            vm.OverrideProxyPort = 1080;
            vm.OverrideProxyUsername = "me";
            vm.OverrideProxyDomain = "CORP";
            vm.OverrideProxyPassword = "hunter2";
        });

        saved.NewDownloadUseProxyOverride.Should().BeTrue();
        saved.NewDownloadProxyKind.Should().Be(ProxyKind.Socks5);
        saved.NewDownloadProxyHost.Should().Be("proxy.example");
        saved.NewDownloadProxyPort.Should().Be(1080);
        saved.NewDownloadProxyUsername.Should().Be("me");
        saved.NewDownloadProxyDomain.Should().Be("CORP");
        saved.NewDownloadProxyPasswordSecretRef.Should().Be("ref-1");

        // §5: only the opaque reference is persisted — the password itself never lands in settings.
        SettingsSerializer.ToStorage(saved).Values.Should().NotContain("hunter2");

        h.Settings.Current.Returns(saved);
        var next = h.Build();
        next.OverrideProxyHost.Should().Be("proxy.example");
        next.OverrideProxyPassword.Should().BeEmpty("the stored password is never re-displayed");
        next.HasStoredProxyPassword.Should().BeTrue();
    }

    [Fact]
    public async Task BlankPasswordField_ReusesTheStoredSecret()
    {
        var h = new Harness();
        h.Settings.Current.Returns(new AppSettings
        {
            ConnectionsPerDownload = 8,
            NewDownloadUseProxyOverride = true,
            NewDownloadProxyHost = "proxy.example",
            NewDownloadProxyPort = 1080,
            NewDownloadProxyUsername = "me",
            NewDownloadProxyPasswordSecretRef = "ref-1",
        });
        h.Secrets.RetrieveAsync("ref-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("hunter2"));

        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        vm.FileName = "file.bin";
        await vm.DownloadNowCommand.ExecuteAsync(null);

        await h.Manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.Proxy!.Credentials!.Password == "hunter2"),
            Arg.Any<CancellationToken>());
        await h.Secrets.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UntouchedFolder_IsNotRemembered_SoDetectionStillApplies()
    {
        var h = new Harness();
        h.SetProbe("clip.mp4", 1000, ranges: true);

        AppSettings saved = await SubmitAndCaptureAsync(h, _ => { });

        saved.NewDownloadFolder.Should().BeNull("the user never chose a folder, so none is pinned");

        // With nothing pinned, the next dialog still re-targets the folder from the probed category.
        h.Settings.Current.Returns(saved);
        var next = h.Build();
        next.Url = "https://host.example/clip.mp4";
        await next.DetectAsync();
        next.SaveToFolder.Should().Be(@"C:\Users\me\Downloads\Program");
    }

    [Fact]
    public async Task RememberedFolder_SurvivesDetection()
    {
        var h = new Harness();
        h.Settings.Current.Returns(new AppSettings
        {
            ConnectionsPerDownload = 8,
            NewDownloadFolder = @"D:\Pinned",
        });
        h.SetProbe("clip.mp4", 1000, ranges: true);

        var vm = h.Build();
        vm.SaveToFolder.Should().Be(@"D:\Pinned");

        vm.Url = "https://host.example/clip.mp4";
        await vm.DetectAsync();

        vm.SaveToFolder.Should().Be(@"D:\Pinned", "a folder restored from a previous run counts as pinned");
    }

    [Fact]
    public async Task RememberedCategory_TargetsItsFolder_WhenNoFolderWasPinned()
    {
        var h = new Harness();
        h.Settings.Current.Returns(new AppSettings
        {
            ConnectionsPerDownload = 8,
            NewDownloadCategory = "Video",
        });

        var vm = h.Build();

        vm.SelectedCategory.Category.Should().Be(FileCategory.Video);
        vm.SaveToFolder.Should().Be(@"C:\Users\me\Downloads\Video");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotLoseTheDownload()
    {
        var h = new Harness();
        h.Settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>())
            .Returns<Task<AppSettings>>(_ => throw new InvalidOperationException("keychain unavailable"));

        var vm = h.Build();
        vm.Url = "https://host.example/file.bin";
        vm.FileName = "file.bin";
        bool closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.DownloadNowCommand.ExecuteAsync(null);

        closed.Should().BeTrue("remembering preferences is best-effort and must not fail the enqueue");
        await h.Manager.Received(1).EnqueueAsync(
            Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
    }
}
