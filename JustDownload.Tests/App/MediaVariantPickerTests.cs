using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.Formatting;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The add-video / quality picker (TASK-060, US-10): it lists the variants extracted for a media URL (AC0)
/// and pre-selects the user's default quality and container (AC1), degrading to a message for progressive
/// or unrecognised URLs.
/// </summary>
public sealed class MediaVariantPickerTests
{
    private static readonly Uri MediaUrl = new("https://cdn.example.com/master.m3u8");

    private static IMediaExtractorRegistry RegistryReturning(MediaSource? source) =>
        RegistryFor(new MediaExtractionResult
        {
            Source = source,
            Attempts = source is null
                ? [MediaExtractionAttempt.Declined("progressive")]
                : [MediaExtractionAttempt.Accepted(source.ExtractorName)],
        });

    private static IMediaExtractorRegistry RegistryFor(MediaExtractionResult result)
    {
        var registry = Substitute.For<IMediaExtractorRegistry>();
        registry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return registry;
    }

    private static MediaExtractionResult NothingFound(params MediaExtractionAttempt[] attempts) =>
        new() { Attempts = attempts };

    private static ISettingsService SettingsWith(VideoQuality quality, MediaContainer container)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { DefaultVideoQuality = quality, DefaultContainer = container });
        return settings;
    }

    private static ITosNoticeGate AlwaysAllows()
    {
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        return gate;
    }

    private static MediaVariantPickerViewModel Build(
        IMediaExtractorRegistry registry,
        ISettingsService settings,
        IDownloadManager? manager = null,
        IDownloadActions? actions = null,
        ITosNoticeGate? tosGate = null,
        IGlobalErrorHandler? errors = null)
    {
        var folders = Substitute.For<IDownloadFolderProvider>();
        folders.GetFolderForCategory(Arg.Any<FileCategory>()).Returns(@"C:\Downloads\Video");
        return new MediaVariantPickerViewModel(
            registry, settings,
            manager ?? Substitute.For<IDownloadManager>(),
            actions ?? Substitute.For<IDownloadActions>(),
            folders,
            tosGate ?? AlwaysAllows(),
            errors ?? Substitute.For<IGlobalErrorHandler>(),
            NullLogger<MediaVariantPickerViewModel>.Instance,
            Substitute.For<IProcessLauncher>());
    }

    private static MediaSource HlsSource(params int[] heights) => new()
    {
        ExtractorName = "hls",
        Kind = MediaKind.Hls,
        Url = MediaUrl,
        Variants = heights.Select(h => new VideoVariant($"https://cdn/{h}.m3u8", h, h * 3000L)).ToArray(),
    };

    [Fact]
    public async Task LoadAsync_ListsAvailableVariants()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 720, 1080)), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.HasVariants.Should().BeTrue();
        vm.Variants.Select(v => v.Variant.Height).Should().Equal(360, 720, 1080);
        vm.Variants[2].Label.Should().Contain("1080p");
    }

    [Fact]
    public async Task LoadAsync_VariantsWithCodecAndFps_LabelDistinguishesOtherwiseIdenticalRenditions()
    {
        // TASK-166: yt-dlp's raw formats often contain several genuinely distinct 720p renditions — the
        // label must show codec and, above 30fps, the frame rate too, so they aren't indistinguishable.
        var source = new MediaSource
        {
            ExtractorName = "yt-dlp",
            Kind = MediaKind.Progressive,
            Url = MediaUrl,
            Variants =
            [
                new VideoVariant("v-h264-30", 720, 1_000_000, 30, "H.264"),
                new VideoVariant("v-vp9-30", 720, 600_000, 30, "VP9"),
                new VideoVariant("v-h264-60", 720, 1_900_000, 60, "H.264"),
            ],
        };
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Variants.Should().HaveCount(3, "no variant is dropped or deduped even though all three are 720p");
        vm.Variants.Select(v => v.Label).Should().Equal(
            "720p · H.264 · 1.0 Mbps",
            "720p · VP9 · 0.6 Mbps",
            "720p60 · H.264 · 1.9 Mbps");
    }

    [Fact]
    public async Task LoadAsync_VariantWithCodecButNoFps_OmitsTheFpsSuffix()
    {
        var source = new MediaSource
        {
            ExtractorName = "yt-dlp",
            Kind = MediaKind.Progressive,
            Url = MediaUrl,
            Variants = [new VideoVariant("v", 720, 1_000_000, null, "H.264")],
        };
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Variants[0].Label.Should().Be("720p · H.264 · 1.0 Mbps", "an unknown fps must not print as 30 or as a bogus suffix");
    }

    [Fact]
    public async Task LoadAsync_VariantWithFpsAtOrBelowThirty_OmitsTheFpsSuffix()
    {
        var source = new MediaSource
        {
            ExtractorName = "yt-dlp",
            Kind = MediaKind.Progressive,
            Url = MediaUrl,
            Variants = [new VideoVariant("v", 720, 1_000_000, 25, "H.264")],
        };
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Variants[0].Label.Should().Be("720p · H.264 · 1.0 Mbps", "30fps and below is the common default — only above 30 is worth calling out");
    }

    [Fact]
    public async Task LoadAsync_VariantWithUnrecognizedCodecLabel_IncludesItVerbatim()
    {
        var source = new MediaSource
        {
            ExtractorName = "yt-dlp",
            Kind = MediaKind.Progressive,
            Url = MediaUrl,
            Variants = [new VideoVariant("v", 480, 500_000, null, "theora")],
        };
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Variants[0].Label.Should().Be("480p · theora · 0.5 Mbps", "an unrecognized codec still falls back to the raw string");
    }

    [Fact]
    public async Task LoadAsync_VariantWithNoCodecOrFps_FallsBackToTheOldHeightAndBitrateLabel()
    {
        // In-house DASH/HLS extractors don't report codec/fps — must degrade gracefully to the pre-TASK-166 format.
        var source = new MediaSource
        {
            ExtractorName = "hls",
            Kind = MediaKind.Hls,
            Url = MediaUrl,
            Variants = [new VideoVariant("v", 1080, 2_500_000)],
        };
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Variants[0].Label.Should().Be("1080p · 2.5 Mbps");
    }

    [Fact]
    public async Task LoadAsync_PreSelectsDefaultQuality()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 720, 1080)), SettingsWith(VideoQuality.P720, MediaContainer.Mp4));

        await vm.LoadAsync(MediaUrl);

        vm.SelectedVariant!.Variant.Height.Should().Be(720, "the default quality (720p) is pre-selected");
        vm.SelectedContainer.Should().Be(MediaContainer.Mp4, "the default container is pre-selected");
    }

    [Fact]
    public async Task LoadAsync_DefaultQualityAboveAll_FallsBackToHighest()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 480)), SettingsWith(VideoQuality.P2160, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.SelectedVariant!.Variant.Height.Should().Be(480, "the closest available at-or-below, i.e. the highest");
    }

    [Fact]
    public async Task LoadAsync_SeparateStreams_ListsAudioRenditions()
    {
        var source = new MediaSource
        {
            ExtractorName = "dash",
            Kind = MediaKind.SeparateStreams,
            Url = MediaUrl,
            Variants = [new VideoVariant("v1080", 1080, 2_500_000)],
            AudioVariants = [new AudioVariant("a-en", 128_000, "en"), new AudioVariant("a-fr", 96_000, "fr")],
        };
        MediaVariantPickerViewModel vm = Build(RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.HasAudio.Should().BeTrue();
        vm.AudioVariants.Should().HaveCount(2);
        vm.SelectedAudio!.Variant.Language.Should().Be("en");
    }

    [Fact]
    public async Task LoadAsync_SeparateStreams_PreSelectsHighestBitrateAudio()
    {
        // The lower-bitrate rendition is listed first — a plain FirstOrDefault() would wrongly pick it.
        var source = new MediaSource
        {
            ExtractorName = "dash",
            Kind = MediaKind.SeparateStreams,
            Url = MediaUrl,
            Variants = [new VideoVariant("v1080", 1080, 2_500_000)],
            AudioVariants = [new AudioVariant("a-low", 96_000, "en"), new AudioVariant("a-high", 192_000, "en")],
        };
        MediaVariantPickerViewModel vm = Build(RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.SelectedAudio!.Variant.Id.Should().Be("a-high", "the highest-bitrate rendition is pre-selected, not the first listed");
    }

    [Fact]
    public async Task LoadAsync_Progressive_ShowsDirectDownloadMessage()
    {
        var source = new MediaSource
        {
            ExtractorName = "progressive",
            Kind = MediaKind.Progressive,
            Url = new Uri("https://cdn/clip.mp4"),
        };
        MediaVariantPickerViewModel vm = Build(RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://cdn/clip.mp4"));

        vm.HasVariants.Should().BeFalse("a one-entry quality dropdown is noise — the message already says it all");
        vm.Message.Should().Contain("direct download");
        vm.CanConfirm.Should().BeTrue(
            "TASK-239: the source URL is itself the stream, so the Download button must be usable — it used "
            + "to stay greyed out forever, making every Facebook/progressive result undownloadable");
    }

    [Fact]
    public async Task ConfirmAsync_ProgressiveWithNoVariants_EnqueuesTheSourceUrl()
    {
        // TASK-239: the only enqueue path builds the request from SelectedVariant, so a zero-variant source
        // has to synthesise one from its own URL (mirroring NewDownloadViewModel.ApplyMediaDetection).
        var source = new MediaSource
        {
            ExtractorName = "facebook",
            Kind = MediaKind.Progressive,
            Url = new Uri("https://video.fbcdn.net/v/clip.mp4"),
            SuggestedFileName = "My reel",
        };
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(11L);
        var actions = Substitute.For<IDownloadActions>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mp4), manager, actions);
        await vm.LoadAsync(source.Url);

        await vm.ConfirmAsync();

        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.Url == new Uri("https://video.fbcdn.net/v/clip.mp4")
                && r.MediaKind == MediaKind.Progressive
                && r.MediaAudioUrl == null
                && r.FileName == "My reel.mp4"
                && r.DestinationDirectory == @"C:\Downloads\Video"),
            Arg.Any<CancellationToken>());
        actions.Received(1).Start(11L);
    }

    [Fact]
    public async Task ConfirmAsync_HlsMediaPlaylistWithNoVariants_EnqueuesItAsHls()
    {
        // A media (non-master) playlist carries no variants to choose between, but HlsDownloader downloads it
        // directly — it must still route through the media path rather than be undownloadable (TASK-239).
        var source = new MediaSource
        {
            ExtractorName = "hls",
            Kind = MediaKind.Hls,
            Url = new Uri("https://cdn.example.com/media.m3u8"),
        };
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(12L);
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv), manager);

        await vm.LoadAsync(source.Url);

        vm.CanConfirm.Should().BeTrue();
        vm.Message.Should().NotBeNullOrWhiteSpace(
            "x.com used to show no message at all next to a dead Download button");
        vm.Message.Should().NotContain("direct download", "an adaptive stream is not a plain file download");

        await vm.ConfirmAsync();

        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.Url == new Uri("https://cdn.example.com/media.m3u8")
                && r.MediaKind == MediaKind.Hls
                && r.FileName == "media.mkv"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_WithVariants_StillEnqueuesTheChosenVariant_NotTheSourceUrl()
    {
        // Guards the TASK-239 no-variant fallback from hijacking the normal, variant-bearing path.
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(13L);
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 720, 1080)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv), manager);
        await vm.LoadAsync(MediaUrl);

        vm.SelectedVariant!.Variant.Id.Should().Be("https://cdn/720.m3u8", "the default quality is still pre-selected");

        await vm.ConfirmAsync();

        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r => r.Url == new Uri("https://cdn/720.m3u8")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_FailedLoadAfterASuccessfulOne_AnnouncesTheEmptiedQualityList()
    {
        // TASK-239: HasVariants is computed over an ObservableCollection whose Clear() raises nothing for it,
        // and only the success path re-announced it — so after a failed re-detect the previous load's Quality
        // ComboBox stayed on screen, empty.
        var registry = Substitute.For<IMediaExtractorRegistry>();
        registry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new MediaExtractionResult
            {
                Source = HlsSource(720, 1080),
                Attempts = [MediaExtractionAttempt.Accepted("hls")],
            }),
            Task.FromResult(NothingFound(MediaExtractionAttempt.Declined("hls"))));
        MediaVariantPickerViewModel vm = Build(registry, SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));
        await vm.LoadAsync(MediaUrl);
        vm.HasVariants.Should().BeTrue("the first load succeeds");

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        await vm.LoadAsync(new Uri("https://example.com/page.html"));

        vm.HasVariants.Should().BeFalse();
        vm.CanConfirm.Should().BeFalse();
        notified.Should().Contain(nameof(MediaVariantPickerViewModel.HasVariants),
            "the view only re-reads a computed property when it is told to");
    }

    [Fact]
    public async Task LoadAsync_NoMedia_ShowsCouldntFindMessage()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(null), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://example.com/page.html"));

        vm.HasVariants.Should().BeFalse();
        vm.Message.Should().Contain("Couldn't find");
    }

    [Fact]
    public async Task LoadAsync_TosNoticeDeclined_DoesNotExtract()
    {
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(1080));
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P1080, MediaContainer.Mkv), tosGate: gate);

        await vm.LoadAsync(MediaUrl);

        await registry.DidNotReceive().ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
        vm.HasVariants.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_TosNoticeAccepted_Extracts()
    {
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(1080));
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P1080, MediaContainer.Mkv), tosGate: gate);

        await vm.LoadAsync(MediaUrl);

        await registry.Received(1).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
        vm.HasVariants.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Hls_EnqueuesMediaDownload_AndStartsIt()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(42L);
        var actions = Substitute.For<IDownloadActions>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(1080)), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv), manager, actions);
        await vm.LoadAsync(MediaUrl);

        bool closedEnqueued = false;
        vm.CloseRequested += (_, ok) => closedEnqueued = ok;

        await vm.ConfirmAsync();

        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == MediaKind.Hls
                && r.Url == new Uri("https://cdn/1080.m3u8")
                && r.MediaAudioUrl == null
                && r.MediaContainer == MediaContainer.Mkv
                && r.FileName == "1080.mkv"
                && r.DestinationDirectory == @"C:\Downloads\Video"),
            Arg.Any<CancellationToken>());
        actions.Received(1).Start(42L);
        closedEnqueued.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_SeparateStreams_IncludesTheAudioUrl()
    {
        var source = new MediaSource
        {
            ExtractorName = "dash",
            Kind = MediaKind.SeparateStreams,
            Url = MediaUrl,
            SuggestedFileName = "clip",
            Variants = [new VideoVariant("https://cdn/video", 1080, 2_500_000)],
            AudioVariants = [new AudioVariant("https://cdn/audio", 128_000, "en")],
        };
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>()).Returns(5L);
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(source), SettingsWith(VideoQuality.P1080, MediaContainer.Mp4), manager);
        await vm.LoadAsync(MediaUrl);

        await vm.ConfirmAsync();

        await manager.Received(1).EnqueueAsync(
            Arg.Is<EnqueueDownloadRequest>(r =>
                r.MediaKind == MediaKind.SeparateStreams
                && r.Url == new Uri("https://cdn/video")
                && r.MediaAudioUrl == new Uri("https://cdn/audio")
                && r.MediaContainer == MediaContainer.Mp4
                && r.FileName == "clip.mp4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_WithNoSelection_DoesNotEnqueue()
    {
        var manager = Substitute.For<IDownloadManager>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(null), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv), manager);
        await vm.LoadAsync(new Uri("https://example.com/page.html")); // no media -> no selection

        bool closedEnqueued = true;
        vm.CloseRequested += (_, ok) => closedEnqueued = ok;

        await vm.ConfirmAsync();

        await manager.DidNotReceive().EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>());
        closedEnqueued.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task PickerWindow_MountsAndShowsVariants()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(480, 1080)), SettingsWith(VideoQuality.P1080, MediaContainer.Mkv));
        await vm.LoadAsync(MediaUrl);

        var window = new MediaVariantPickerWindow { DataContext = vm };
        window.Show();

        window.IsVisible.Should().BeTrue();
        vm.HasVariants.Should().BeTrue();
    }

    /// <summary>
    /// Stands in for the real notice's side effect: showing a modal pulls focus off the URL box, and the
    /// view commits the URL on focus loss — calling straight back into <c>LoadAsync</c> while this notice is
    /// still open (TASK-234).
    /// </summary>
    private sealed class ReentrantGate : ITosNoticeGate
    {
        public MediaVariantPickerViewModel? ViewModel { get; set; }

        public int Shown { get; private set; }

        public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
        {
            Shown++;
            await ViewModel!.LoadAsync(MediaUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    [Fact]
    public async Task LoadAsync_RaisesOnlyOneNotice_WhenShowingItReCommitsTheUrl()
    {
        // TASK-234, user-reported: the ToS notice stacked two-deep, the second owned by the first, because
        // showing it stole focus and the focus-loss handler started a whole second extraction.
        var gate = new ReentrantGate();
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(720));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv), tosGate: gate);
        gate.ViewModel = vm;

        await vm.LoadAsync(MediaUrl);

        gate.Shown.Should().Be(1, "a second notice must never be raised while the first one is still open");
        await registry.Received(1).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
        vm.HasVariants.Should().BeTrue("the original extraction still completes normally");
    }

    [Fact]
    public async Task DetectAsync_ReportsAndExplains_WhenExtractionBlowsUp()
    {
        // TASK-235: every caller starts DetectAsync from a UI event and none of them await it, so it must
        // absorb its own failures. This is the real regression from TASK-233: the ToS notice threw "Cannot
        // show window with non-visible owner" and the user got a blank picker with no explanation, the
        // failure reaching the log only as a delayed UnobservedTaskException.
        var boom = new InvalidOperationException("Cannot show window with non-visible owner.");
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw boom);
        var errors = Substitute.For<IGlobalErrorHandler>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            tosGate: gate, errors: errors);
        vm.Url = MediaUrl.AbsoluteUri;

        await vm.DetectAsync(); // must not throw

        errors.Received(1).Handle(boom, Arg.Any<string>());
        vm.Message.Should().NotBeNullOrWhiteSpace("the user has to be told why the picker is empty");
        vm.CanConfirm.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_SkipsAnUnchangedUrl_SoTheNoticeIsNotRaisedTwice()
    {
        // TASK-237, user-reported: the URL box commits on every focus loss, so extraction ran twice for one
        // URL and the ToS notice reappeared after the user had already agreed to it.
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(720));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv), tosGate: gate);
        vm.Url = MediaUrl.AbsoluteUri;

        await vm.DetectAsync();
        await vm.DetectAsync();
        await vm.DetectAsync();

        await gate.Received(1).ConfirmAsync(Arg.Any<CancellationToken>());
        await registry.Received(1).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_SkipsAnUnchangedUrl_EvenAfterTheNoticeWasDeclined()
    {
        // Closing the notice with X counts as declining; a stray focus change must not re-ask.
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            tosGate: gate);
        vm.Url = MediaUrl.AbsoluteUri;

        await vm.DetectAsync();
        await vm.DetectAsync();

        await gate.Received(1).ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_RunsAgain_WhenTheUrlActuallyChanges()
    {
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(720));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        vm.Url = MediaUrl.AbsoluteUri;
        await vm.DetectAsync();
        vm.Url = "https://cdn.example.com/other.m3u8";
        await vm.DetectAsync();

        await registry.Received(2).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedetectAsync_RunsAgain_ForTheSameUrl()
    {
        // Enter is explicit, so it must work even after a declined notice — otherwise the only way to retry
        // would be to edit a URL that was correct all along.
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(720));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        vm.Url = MediaUrl.AbsoluteUri;

        await vm.DetectAsync();
        await vm.RedetectAsync();

        await registry.Received(2).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanConfirm_NotifiesSoTheDownloadButtonEnables()
    {
        // TASK-237: CanConfirm is computed and nothing announced it, so the Download button — bound to it
        // with IsEnabled="{Binding CanConfirm}" — stayed disabled even with a quality selected. The existing
        // ConfirmAsync tests never caught this because they call the view-model directly.
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        await vm.LoadAsync(MediaUrl);

        vm.CanConfirm.Should().BeTrue();
        notified.Should().Contain(nameof(MediaVariantPickerViewModel.CanConfirm),
            "the view only re-reads a computed property when it is told to");
    }

    [Fact]
    public async Task ConfirmAsync_ReportsAndKeepsTheDialogOpen_WhenEnqueueFails()
    {
        // TASK-236: the view calls this from an async void click handler, so an escaping exception is fatal
        // rather than merely silent — it reaches AppDomain.UnhandledException and kills the app. Enqueue does
        // real DB work, so it can genuinely fail (disk full, locked database, ...).
        var boom = new IOException("database is locked");
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw boom);
        var errors = Substitute.For<IGlobalErrorHandler>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            manager: manager, errors: errors);
        await vm.LoadAsync(MediaUrl);
        bool closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        await vm.ConfirmAsync(); // must not throw

        errors.Received(1).Handle(boom, Arg.Any<string>());
        vm.Message.Should().NotBeNullOrWhiteSpace("the user has to be told the download did not start");
        closed.Should().BeFalse("the dialog stays open so the quality choice is not lost on a retry");
    }

    [Fact]
    public async Task ConfirmAsync_StillClosesWithSuccess_WhenEnqueueWorks()
    {
        // Guards the catch above from swallowing the happy path.
        var manager = Substitute.For<IDownloadManager>();
        manager.EnqueueAsync(Arg.Any<EnqueueDownloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(7L));
        var errors = Substitute.For<IGlobalErrorHandler>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            manager: manager, errors: errors);
        await vm.LoadAsync(MediaUrl);
        bool? closedEnqueued = null;
        vm.CloseRequested += (_, ok) => closedEnqueued = ok;

        await vm.ConfirmAsync();

        closedEnqueued.Should().BeTrue();
        errors.DidNotReceive().Handle(Arg.Any<Exception>(), Arg.Any<string>());
        vm.Message.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_StaysSilent_WhenTheLoadIsCanceled()
    {
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new OperationCanceledException());
        var errors = Substitute.For<IGlobalErrorHandler>();
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            tosGate: gate, errors: errors);
        vm.Url = MediaUrl.AbsoluteUri;

        await vm.DetectAsync();

        errors.DidNotReceive().Handle(Arg.Any<Exception>(), Arg.Any<string>());
        vm.Message.Should().BeNull("an abandoned load is not a failure to report");
    }

    [Fact]
    public async Task LoadAsync_RunsAgain_OnceTheEarlierAttemptHasFinished()
    {
        // The guard must be a re-entrancy guard, not a one-shot latch — re-detecting after the first pass
        // finished is exactly what the URL box's commit is for.
        var gate = Substitute.For<ITosNoticeGate>();
        gate.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IMediaExtractorRegistry registry = RegistryReturning(HlsSource(720));
        MediaVariantPickerViewModel vm = Build(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv), tosGate: gate);

        await vm.LoadAsync(MediaUrl);
        await vm.LoadAsync(MediaUrl);

        await registry.Received(2).ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>());
    }

    // --- Why extraction failed (the picker must stop collapsing every cause into one generic string) ---

    [Fact]
    public async Task LoadAsync_EveryExtractorDeclined_KeepsTheGenericNoMediaMessage()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(
                MediaExtractionAttempt.Declined("hls"),
                MediaExtractionAttempt.Declined("progressive"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://example.com/article"));

        vm.Message.Should().Be("Couldn't find downloadable media at this URL.",
            "nothing recognised the URL, which is exactly what the original wording says");
    }

    [Fact]
    public async Task LoadAsync_NetworkFailure_SaysSo_NeverThatNoMediaWasFound()
    {
        // The user's NextDNS resolver null-routes facebook.com, so extraction dies on DNS. Reporting that as
        // "couldn't find downloadable media" sent them hunting for a broken extractor instead of a broken DNS.
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(
                MediaExtractionAttempt.Declined("hls"),
                MediaExtractionAttempt.NetworkFailure("facebook", "HttpRequestException: No such host is known."))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://www.facebook.com/reel/2044478973099445"));

        vm.Message.Should().Contain("Network error").And.Contain("www.facebook.com");
        vm.Message.Should().NotContain("Couldn't find downloadable media");
    }

    [Fact]
    public async Task LoadAsync_ExtractorFailedWithAReason_ShowsItAttributedToThatExtractor()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(
                MediaExtractionAttempt.Declined("progressive"),
                MediaExtractionAttempt.Failed("yt-dlp", "Sign in to confirm you're not a bot"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://www.youtube.com/watch?v=CqlDf9ba4jA"));

        vm.Message.Should().Contain("yt-dlp: Sign in to confirm you're not a bot");
    }

    [Fact]
    public async Task LoadAsync_JsRuntimeFailure_SetsInstallJsRuntimeHint()
    {
        // AC2: the raw reason still shows (above), but a clickable hint is added for the known, fixable cause.
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(
                MediaExtractionAttempt.Failed(
                    "yt-dlp",
                    "No supported JavaScript runtime could be found. YouTube extraction without a JS runtime has been deprecated"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://www.youtube.com/watch?v=CqlDf9ba4jA"));

        vm.Hint.Should().NotBeNull();
        vm.Hint!.Kind.Should().Be(ExtractionHintKind.InstallJsRuntime);
        vm.Hint.ActionUri.Should().StartWith("https://deno.land/");
    }

    [Fact]
    public async Task LoadAsync_EveryExtractorDeclined_LeavesHintNull()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(
                MediaExtractionAttempt.Declined("hls"),
                MediaExtractionAttempt.Declined("progressive"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(new Uri("https://example.com/article"));

        vm.Hint.Should().BeNull("a plain decline has no actionable hint");
    }

    [Fact]
    public async Task OpenHintAsync_InvokesProcessLauncherWithTheHintUri()
    {
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = RegistryFor(NothingFound(
            MediaExtractionAttempt.Failed(
                "yt-dlp",
                "No supported JavaScript runtime could be found. YouTube extraction without a JS runtime has been deprecated")));
        var folders = Substitute.For<IDownloadFolderProvider>();
        folders.GetFolderForCategory(Arg.Any<FileCategory>()).Returns(@"C:\Downloads\Video");
        var vm = new MediaVariantPickerViewModel(
            registry, SettingsWith(VideoQuality.P720, MediaContainer.Mkv),
            Substitute.For<IDownloadManager>(), Substitute.For<IDownloadActions>(), folders,
            AlwaysAllows(), Substitute.For<IGlobalErrorHandler>(),
            NullLogger<MediaVariantPickerViewModel>.Instance, launcher);

        await vm.LoadAsync(new Uri("https://www.youtube.com/watch?v=CqlDf9ba4jA"));
        await vm.OpenHintAsync();

        launcher.Received(1).OpenUrl(Arg.Is<string>(u => u.StartsWith("https://deno.land/")));
    }

    [Fact]
    public async Task LoadAsync_ReasonCarryingASignedUrl_IsNotShownVerbatim()
    {
        // CLAUDE.md §5: these strings are user-facing — signed-URL query strings must never reach them.
        MediaExtractionAttempt leaky = MediaExtractionAttempt.Failed(
            "hls",
            "HTTP 403 fetching https://video.twimg.com/pl/x.m3u8?Signature=SECRET123&Key-Pair-Id=APKA");
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(leaky)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(MediaUrl);

        vm.Message.Should().NotContain("SECRET123").And.NotContain("Signature=").And.NotContain("Key-Pair-Id");
        vm.Message.Should().Contain("hls: HTTP 403 fetching https://video.twimg.com/pl/x.m3u8");
    }

    // --- Falling back to the stream the browser sniffed (TASK-241) ---------------------------------------
    //
    // x.com is routed through extraction because yt-dlp resolves a status page into a real title and every
    // variant. But there is no in-house Twitter extractor, so on an app where yt-dlp is not enabled the page
    // is declined by everything — and before this the user, who previously got a working sniffed download,
    // was left staring at "Couldn't find downloadable media". The extension now sends the stream it saw
    // alongside the page so the picker can offer it.

    private static readonly Uri StatusUrl = new("https://x.com/unicodef1wn/status/2087461469881336049");
    private const string SniffedMaster = "https://video.twimg.com/amplify_video/2087461469881336049/pl/9k3T.m3u8";

    private static IMediaExtractorRegistry RegistryPerUrl(
        Uri firstUrl, MediaExtractionResult firstResult, Uri secondUrl, MediaExtractionResult secondResult)
    {
        var registry = Substitute.For<IMediaExtractorRegistry>();
        registry.ExtractAsync(Arg.Is<MediaRequest>(r => r.Url == firstUrl), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(firstResult));
        registry.ExtractAsync(Arg.Is<MediaRequest>(r => r.Url == secondUrl), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secondResult));
        return registry;
    }

    private static MediaExtractionResult Found(MediaSource source) =>
        new() { Source = source, Attempts = [MediaExtractionAttempt.Accepted(source.ExtractorName)] };

    [Fact]
    public async Task LoadAsync_ExtractionFoundNothing_OffersTheSniffedStream()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(MediaExtractionAttempt.Declined("hls"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        vm.FallbackUrl = SniffedMaster;

        await vm.LoadAsync(StatusUrl);

        vm.CanUseFallback.Should().BeTrue("the browser plainly had a playable stream — this must not dead-end");
    }

    [Fact]
    public async Task LoadAsync_ExtractionSucceeded_DoesNotOfferTheFallback()
    {
        MediaVariantPickerViewModel vm = Build(
            RegistryReturning(HlsSource(360, 720)), SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        vm.FallbackUrl = SniffedMaster;

        await vm.LoadAsync(StatusUrl);

        vm.CanUseFallback.Should().BeFalse("there is nothing to fall back from");
    }

    [Fact]
    public async Task LoadAsync_NoFallbackSent_OffersNothing()
    {
        // A picker the user opened themselves, or a hand-off where the sniffer saw nothing.
        MediaVariantPickerViewModel vm = Build(
            RegistryFor(NothingFound(MediaExtractionAttempt.Declined("hls"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));

        await vm.LoadAsync(StatusUrl);

        vm.CanUseFallback.Should().BeFalse();
    }

    [Fact]
    public async Task UseFallbackAsync_ExtractsTheSniffedStream_AndListsItsVariants()
    {
        var sniffed = new Uri(SniffedMaster);
        MediaVariantPickerViewModel vm = Build(
            RegistryPerUrl(
                StatusUrl,
                NothingFound(MediaExtractionAttempt.Declined("hls"), MediaExtractionAttempt.Declined("progressive")),
                sniffed,
                Found(HlsSource(270, 480, 720))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        vm.FallbackUrl = SniffedMaster;

        await vm.LoadAsync(StatusUrl);
        await vm.UseFallbackAsync();

        vm.Url.Should().Be(SniffedMaster);
        // The sniffed URL is a master playlist, so every quality is offered — not one fixed rendition.
        vm.Variants.Select(v => v.Variant.Height).Should().Equal(270, 480, 720);
        vm.SelectedVariant!.Variant.Height.Should().Be(720, "the user's default quality still applies");
        vm.CanUseFallback.Should().BeFalse("the offer is spent once taken");
    }

    [Fact]
    public async Task UseFallbackAsync_FallbackAlsoFails_DoesNotOfferItselfAgain()
    {
        var sniffed = new Uri(SniffedMaster);
        MediaVariantPickerViewModel vm = Build(
            RegistryPerUrl(
                StatusUrl,
                NothingFound(MediaExtractionAttempt.Declined("hls")),
                sniffed,
                NothingFound(MediaExtractionAttempt.Failed("hls", "HTTP 403"))),
            SettingsWith(VideoQuality.P720, MediaContainer.Mkv));
        vm.FallbackUrl = SniffedMaster;

        await vm.LoadAsync(StatusUrl);
        await vm.UseFallbackAsync();

        vm.Message.Should().Contain("hls: HTTP 403");
        vm.CanUseFallback.Should().BeFalse("re-offering the URL that just failed would loop the user in place");
    }
}
