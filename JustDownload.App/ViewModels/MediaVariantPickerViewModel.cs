using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.ViewModels;

/// <summary>The user's choice from the quality picker (TASK-060): video + optional audio + container.</summary>
/// <param name="Video">The chosen video variant (its <see cref="VideoVariant.Id"/> is the stream URL).</param>
/// <param name="Audio">The chosen audio rendition for separate streams, or <see langword="null"/>.</param>
/// <param name="Container">The chosen output container.</param>
public sealed record MediaVariantSelection(VideoVariant Video, AudioVariant? Audio, MediaContainer Container);

/// <summary>One selectable video quality, with a human label for the picker (TASK-060).</summary>
/// <param name="Variant">The underlying engine variant (its <see cref="VideoVariant.Id"/> is the stream URL).</param>
/// <param name="Label">A display label, e.g. <c>"1080p · 2.5 Mbps"</c>.</param>
public sealed record VariantOption(VideoVariant Variant, string Label);

/// <summary>One selectable audio rendition, with a human label (TASK-060).</summary>
/// <param name="Variant">The underlying engine audio variant.</param>
/// <param name="Label">A display label, e.g. <c>"en · 128 kbps"</c>.</param>
public sealed record AudioOption(AudioVariant Variant, string Label);

/// <summary>
/// The add-video / quality picker (TASK-060, US-10). Given a media URL it runs the extractor registry to
/// list the available video qualities (and audio renditions for separate streams), then pre-selects the
/// quality matching the user's <see cref="AppSettings.DefaultVideoQuality"/> (via
/// <see cref="VideoQualitySelector"/>) and the <see cref="AppSettings.DefaultContainer"/>. Progressive or
/// unrecognised URLs degrade to a clear message rather than an empty list. Pure view-model logic so it is
/// unit-testable; the window is a thin shell.
/// </summary>
public sealed partial class MediaVariantPickerViewModel : ViewModelBase
{
    private readonly IMediaExtractorRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly IDownloadManager _manager;
    private readonly IDownloadActions _actions;
    private readonly IDownloadFolderProvider _folders;
    private readonly ILogger<MediaVariantPickerViewModel> _logger;
    private MediaSource? _source;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private MediaKind? _kind;

    [ObservableProperty]
    private VariantOption? _selectedVariant;

    [ObservableProperty]
    private AudioOption? _selectedAudio;

    [ObservableProperty]
    private MediaContainer _selectedContainer;

    public MediaVariantPickerViewModel(
        IMediaExtractorRegistry registry,
        ISettingsService settings,
        IDownloadManager manager,
        IDownloadActions actions,
        IDownloadFolderProvider folders,
        ILogger<MediaVariantPickerViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _settings = settings;
        _manager = manager;
        _actions = actions;
        _folders = folders;
        _logger = logger;
        _selectedContainer = settings.Current.DefaultContainer;
    }

    /// <summary>Raised when the dialog should close; <see langword="true"/> when a media download was enqueued.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The video qualities found for the URL, highest-first.</summary>
    public ObservableCollection<VariantOption> Variants { get; } = new();

    /// <summary>The audio renditions found (separate-streams media); empty otherwise.</summary>
    public ObservableCollection<AudioOption> AudioVariants { get; } = new();

    /// <summary>The output containers the user can choose between.</summary>
    public IReadOnlyList<MediaContainer> Containers { get; } =
        [MediaContainer.Mkv, MediaContainer.Mp4, MediaContainer.Webm];

    /// <summary>Whether any selectable video qualities were found.</summary>
    public bool HasVariants => Variants.Count > 0;

    /// <summary>Whether any selectable audio renditions were found (separate-streams media).</summary>
    public bool HasAudio => AudioVariants.Count > 0;

    /// <summary>Whether the current <see cref="Url"/> is a well-formed http(s) URL worth extracting (the view triggers it).</summary>
    public bool CanDetect => TryGetUrl(out _);

    /// <summary>Whether a variant is chosen and can be downloaded.</summary>
    public bool CanConfirm => SelectedVariant is not null && _source is not null;

    /// <summary>Extracts the media at the current <see cref="Url"/> (called by the view on commit).</summary>
    public async Task DetectAsync()
    {
        if (TryGetUrl(out Uri? uri))
        {
            await LoadAsync(uri).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Enqueues the chosen variant as a media download (TASK-100): video stream + optional audio + container,
    /// saved into the Video category folder, then starts it. Raises <see cref="CloseRequested"/>.
    /// </summary>
    public async Task ConfirmAsync()
    {
        if (SelectedVariant is null || _source is null)
        {
            CloseRequested?.Invoke(this, false);
            return;
        }

        string videoUrl = SelectedVariant.Variant.Id;
        var request = new EnqueueDownloadRequest
        {
            Url = new Uri(videoUrl),
            DestinationDirectory = _folders.GetFolderForCategory(FileCategory.Video),
            FileName = MediaFileName(_source.SuggestedFileName, videoUrl, SelectedContainer),
            CategoryType = FileCategory.Video.ToString(),
            MediaKind = Kind,
            MediaAudioUrl = SelectedAudio is { } audio ? new Uri(audio.Variant.Id) : null,
            MediaContainer = SelectedContainer,
        };

        long id = await _manager.EnqueueAsync(request).ConfigureAwait(true);
        _actions.Start(id);
        CloseRequested?.Invoke(this, true);
    }

    private bool TryGetUrl([NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (Uri.TryCreate(Url.Trim(), UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    private static string MediaFileName(string? suggested, string videoUrl, MediaContainer container)
    {
        string baseName = !string.IsNullOrWhiteSpace(suggested)
            ? Path.GetFileNameWithoutExtension(suggested)
            : Path.GetFileNameWithoutExtension(new Uri(videoUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "video";
        }

        string extension = container switch
        {
            MediaContainer.Mp4 => ".mp4",
            MediaContainer.Webm => ".webm",
            _ => ".mkv",
        };
        return baseName + extension;
    }

    /// <summary>
    /// Extracts the media at <paramref name="url"/> and populates the pickers, pre-selecting the user's
    /// default quality and container (AC1). Sets <see cref="Message"/> for progressive/unrecognised media.
    /// </summary>
    public async Task LoadAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        IsLoading = true;
        Message = null;
        _source = null;
        Variants.Clear();
        AudioVariants.Clear();
        SelectedVariant = null;
        SelectedAudio = null;

        try
        {
            MediaSource? source = await _registry
                .ExtractAsync(new MediaRequest { Url = url, Headers = headers ?? [] }, cancellationToken)
                .ConfigureAwait(true);

            if (source is null)
            {
                Message = "Couldn't find downloadable media at this URL.";
                return;
            }

            _source = source;
            Kind = source.Kind;
            foreach (VideoVariant variant in source.Variants)
            {
                Variants.Add(new VariantOption(variant, DescribeVideo(variant)));
            }

            foreach (AudioVariant audio in source.AudioVariants)
            {
                AudioVariants.Add(new AudioOption(audio, DescribeAudio(audio)));
            }

            // Honour the default container (AC1).
            SelectedContainer = _settings.Current.DefaultContainer;

            if (source.Variants.Count > 0)
            {
                // Pre-select the quality matching the user's default (AC1).
                VideoVariant chosen = VideoQualitySelector.Select(source.Variants, _settings.Current.DefaultVideoQuality);
                SelectedVariant = Variants.FirstOrDefault(o => o.Variant == chosen) ?? Variants[0];
            }
            else if (source.Kind == MediaKind.Progressive)
            {
                Message = "This is a direct download — no quality options.";
            }

            SelectedAudio = AudioVariants.FirstOrDefault();
            OnPropertyChanged(nameof(HasVariants));
            OnPropertyChanged(nameof(HasAudio));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogExtractionFailed(_logger, url, ex);
            Message = "Couldn't read the media information for this URL.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string DescribeVideo(VideoVariant variant)
    {
        string quality = variant.Height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{variant.Height}p")
            : "Auto";
        return variant.Bandwidth is { } bps && bps > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{quality} · {bps / 1_000_000.0:0.0} Mbps")
            : quality;
    }

    private static string DescribeAudio(AudioVariant variant)
    {
        string lang = string.IsNullOrWhiteSpace(variant.Language) ? "Audio" : variant.Language!;
        return variant.Bandwidth is { } bps && bps > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{lang} · {bps / 1000} kbps")
            : lang;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Media extraction failed for {Url}.")]
    private static partial void LogExtractionFailed(ILogger logger, Uri url, Exception exception);
}
