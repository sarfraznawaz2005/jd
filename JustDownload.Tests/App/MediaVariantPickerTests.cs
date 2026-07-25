using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
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

    private static IMediaExtractorRegistry RegistryReturning(MediaSource? source)
    {
        var registry = Substitute.For<IMediaExtractorRegistry>();
        registry.ExtractAsync(Arg.Any<MediaRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(source));
        return registry;
    }

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
            NullLogger<MediaVariantPickerViewModel>.Instance);
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

        vm.HasVariants.Should().BeFalse();
        vm.Message.Should().Contain("direct download");
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
}
