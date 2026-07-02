using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
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
        ITosNoticeGate? tosGate = null)
    {
        var folders = Substitute.For<IDownloadFolderProvider>();
        folders.GetFolderForCategory(Arg.Any<FileCategory>()).Returns(@"C:\Downloads\Video");
        return new MediaVariantPickerViewModel(
            registry, settings,
            manager ?? Substitute.For<IDownloadManager>(),
            actions ?? Substitute.For<IDownloadActions>(),
            folders,
            tosGate ?? AlwaysAllows(),
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
}
