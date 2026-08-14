using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Formatting;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Diagnostics;
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
/// <see cref="VideoQualitySelector"/>) and the <see cref="AppSettings.DefaultContainer"/>. A source with no
/// quality list (progressive media, a single HLS media playlist) is still downloadable — its own URL is
/// selected as the stream and a message explains there is nothing to choose; unrecognised URLs degrade to a
/// clear message rather than an empty list. Pure view-model logic so it is unit-testable; the window is a
/// thin shell.
/// </summary>
public sealed partial class MediaVariantPickerViewModel : ViewModelBase
{
    private readonly IMediaExtractorRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly IDownloadManager _manager;
    private readonly IDownloadActions _actions;
    private readonly IDownloadFolderProvider _folders;
    private readonly ITosNoticeGate _tosGate;
    private readonly IGlobalErrorHandler _errors;
    private readonly ILogger<MediaVariantPickerViewModel> _logger;
    private readonly IProcessLauncher _launcher;
    private MediaSource? _source;
    private bool _detecting;
    private Uri? _lastAttemptedUrl;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private ExtractionHint? _hint;

    [ObservableProperty]
    private MediaKind? _kind;

    // Without this the Download button, bound to CanConfirm, never re-evaluated after the picker was built
    // and so stayed disabled even with a quality selected (TASK-237). CanConfirm is computed, so choosing a
    // variant raised no notification of its own.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private VariantOption? _selectedVariant;

    [ObservableProperty]
    private AudioOption? _selectedAudio;

    [ObservableProperty]
    private MediaContainer _selectedContainer;

    [ObservableProperty]
    private bool _canUseFallback;

    public MediaVariantPickerViewModel(
        IMediaExtractorRegistry registry,
        ISettingsService settings,
        IDownloadManager manager,
        IDownloadActions actions,
        IDownloadFolderProvider folders,
        ITosNoticeGate tosGate,
        IGlobalErrorHandler errors,
        ILogger<MediaVariantPickerViewModel> logger,
        IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(tosGate);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(launcher);
        _registry = registry;
        _settings = settings;
        _manager = manager;
        _actions = actions;
        _folders = folders;
        _tosGate = tosGate;
        _errors = errors;
        _logger = logger;
        _launcher = launcher;
        _selectedContainer = settings.Current.DefaultContainer;
    }

    /// <summary>
    /// A stream the browser extension's sniffer saw on the handed-off page, offered when extraction of the
    /// page itself finds nothing (TASK-241). Set by the shell for an extraction hand-off; <see langword="null"/>
    /// for a picker the user opened themselves.
    /// </summary>
    public string? FallbackUrl { get; set; }

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

    /// <summary>
    /// Extracts the media at the current <see cref="Url"/> (called by the view when the URL box commits, and
    /// by the shell for a browser hand-off). Deliberately never throws: every caller starts it from a UI
    /// event and nobody awaits it, so an escaping exception would surface only as a delayed
    /// <c>UnobservedTaskException</c> on the finalizer thread while the user sat looking at an empty picker
    /// with a disabled Download button (§1: no silent failures). Failures are reported through the global
    /// error handler and stated in <see cref="Message"/> instead.
    /// </summary>
    public Task DetectAsync() => DetectAsync(force: false);

    /// <summary>
    /// Re-runs extraction even when the URL has not changed — for an explicit request (the user pressing
    /// Enter in the URL box), where "nothing happened" would be the wrong answer (TASK-237).
    /// </summary>
    public Task RedetectAsync() => DetectAsync(force: true);

    /// <summary>
    /// Extracts <see cref="FallbackUrl"/> instead of the page (TASK-241). Sites with no in-house extractor —
    /// x.com among them — resolve only through the optional yt-dlp fallback (D3), so without it the hand-off
    /// dead-ends on "couldn't find downloadable media" even though the browser was plainly playing something.
    /// The sniffed stream is a master playlist wherever the extension could identify one, so this normally
    /// lands on the full quality list rather than a single fixed rendition.
    /// </summary>
    public Task UseFallbackAsync()
    {
        if (!CanUseFallback || string.IsNullOrWhiteSpace(FallbackUrl))
        {
            return Task.CompletedTask;
        }

        Url = FallbackUrl;
        return RedetectAsync();
    }

    /// <summary>
    /// Opens the hint's target URL (deno.land / the yt-dlp cookies wiki) in the OS default browser. Deliberately
    /// never throws — like the existing async-void-safe affordances, the click is fire-and-forget and an open
    /// failure must not surface as an unobserved exception.
    /// </summary>
    public Task OpenHintAsync()
    {
        if (Hint is { } h && !string.IsNullOrWhiteSpace(h.ActionUri))
        {
            _launcher.OpenUrl(h.ActionUri);
        }

        return Task.CompletedTask;
    }

    private async Task DetectAsync(bool force)
    {
        if (!TryGetUrl(out Uri? uri))
        {
            return;
        }

        // The view commits the URL on *every* focus loss, so the same unchanged URL arrives here again and
        // again — when the picker opens, and each time a dialog or control takes focus. Re-extracting it is
        // pure waste, and since the ToS notice is raised per extraction the user was shown it a second time
        // after having already agreed, and again after dismissing it (TASK-237). Remembered even when the
        // attempt fails or is declined, so a refusal is not re-asked on the next stray focus change.
        if (!force && uri == _lastAttemptedUrl)
        {
            return;
        }

        _lastAttemptedUrl = uri;

        try
        {
            await LoadAsync(uri).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected when a load is abandoned — not a failure worth reporting.
        }
        catch (Exception ex)
        {
            _errors.Handle(ex, nameof(DetectAsync));
            Message = "Couldn't read this page. See the error log for details.";
        }
    }

    /// <summary>
    /// Enqueues the chosen variant as a media download (TASK-100): video stream + optional audio + container,
    /// saved into the Video category folder, then starts it. Raises <see cref="CloseRequested"/>.
    /// <para>
    /// Enqueue failures are reported rather than thrown (TASK-236). The view calls this from an
    /// <c>async void</c> click handler, where an escaping exception is not merely swallowed but *fatal* — it
    /// reaches <see cref="AppDomain.UnhandledException"/> and takes the process down — and this method does
    /// real database and disk work. On failure the dialog deliberately stays open, showing what went wrong,
    /// so the user can retry without re-picking a quality.
    /// </para>
    /// </summary>
    public async Task ConfirmAsync()
    {
        if (SelectedVariant is null || _source is null)
        {
            CloseRequested?.Invoke(this, false);
            return;
        }

        try
        {
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
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _errors.Handle(ex, nameof(ConfirmAsync));
            Message = "Couldn't start this download. See the error log for details.";
            return;
        }

        // Raised outside the guard above: a subscriber's own fault is not an enqueue failure, and reporting
        // it as one would tell the user the download failed when it is already queued and running.
        CloseRequested?.Invoke(this, true);
    }

    // Never offered for the URL that just failed, so retrying the fallback cannot loop back on itself.
    private bool HasUsableFallback(Uri attempted) =>
        Uri.TryCreate(FallbackUrl, UriKind.Absolute, out Uri? fallback)
        && (fallback.Scheme == Uri.UriSchemeHttp || fallback.Scheme == Uri.UriSchemeHttps)
        && fallback != attempted;

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
    /// Gates on the one-time ToS notice (TASK-160, docs/LEGAL.md) then extracts the media at
    /// <paramref name="url"/> and populates the pickers, pre-selecting the user's default quality and
    /// container (AC1). Sets <see cref="Message"/> for progressive/unrecognised media. If the user declines
    /// the notice, returns without ever calling <see cref="IMediaExtractorRegistry.ExtractAsync"/>.
    /// </summary>
    public async Task LoadAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Re-entrancy guard (TASK-234): the view commits the URL — and so calls back in here — whenever the
        // URL box loses focus, and showing any modal takes that focus away. The ToS notice below is exactly
        // such a modal, so without this guard it stole focus, the re-entrant call raised a second notice
        // owned by the first, and the user got a stack of identical dialogs. Covers the gate as well as the
        // extraction, because the window where a second call does damage opens before IsLoading is set.
        if (_detecting)
        {
            return;
        }

        _detecting = true;
        try
        {
            await LoadCoreAsync(url, headers, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _detecting = false;
        }
    }

    private async Task LoadCoreAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        if (!await _tosGate.ConfirmAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        IsLoading = true;
        Message = null;
        Hint = null;
        CanUseFallback = false;
        _source = null;
        Variants.Clear();
        AudioVariants.Clear();
        SelectedVariant = null;
        SelectedAudio = null;

        try
        {
            MediaExtractionResult extraction = await _registry
                .ExtractAsync(new MediaRequest { Url = url, Headers = headers ?? [] }, cancellationToken)
                .ConfigureAwait(true);

            if (extraction.Source is not { } source)
            {
                // Say why, not just "nothing found": a DNS/connectivity failure and a yt-dlp bot challenge
                // used to be indistinguishable from "this page has no video" (CLAUDE.md §5).
                Message = MediaExtractionMessage.Describe(url, extraction.Attempts);
                Hint = ExtractionHintClassifier.Classify(extraction.Attempts);
                CanUseFallback = HasUsableFallback(url);
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
            else
            {
                // A source with no quality list is still downloadable — the source URL *is* the stream
                // (TASK-239). Without this the picker showed a message and a permanently disabled Download
                // button for every Facebook/progressive/single-media-playlist result, so those could never
                // be downloaded at all. Mirrors NewDownloadViewModel.ApplyMediaDetection's
                // `chosenVideo is not null ? new Uri(chosenVideo.Id) : source.Url` fallback, and reuses
                // ConfirmAsync unchanged (it still passes MediaKind, so HLS keeps the segment/mux path).
                // Deliberately not added to Variants: a one-entry quality dropdown is noise, and the
                // message below already says there is nothing to choose.
                SelectedVariant = new VariantOption(new VideoVariant(source.Url.ToString(), 0), "Original");
                Message = source.Kind == MediaKind.Progressive
                    ? "This is a direct download — no quality options."
                    : "Single stream — no quality options.";
            }

            if (AudioVariants.Count > 0)
            {
                // Prefer the highest-bitrate audio rendition (TASK-167).
                AudioVariant chosenAudio = AudioQualitySelector.Select(source.AudioVariants);
                SelectedAudio = AudioVariants.FirstOrDefault(o => o.Variant == chosenAudio) ?? AudioVariants[0];
            }

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

            // Raised on every exit path, not just the successful one (TASK-239): these are computed over
            // the collections cleared above, and ObservableCollection.Clear() announces nothing for them.
            // A failed load after a successful one used to leave the Quality box on screen and empty.
            OnPropertyChanged(nameof(HasVariants));
            OnPropertyChanged(nameof(HasAudio));
            OnPropertyChanged(nameof(CanConfirm)); // _source is a plain field — nothing else announces it
        }
    }

    private static string DescribeVideo(VideoVariant variant)
    {
        string quality = variant.Height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{variant.Height}p")
            : "Auto";

        // Same-resolution renditions can differ by codec/fps (yt-dlp's raw formats, e.g. H.264 vs VP9 vs
        // AV1, or 30fps vs 60fps) — surface both so the picker never shows indistinguishable duplicates
        // (TASK-166). Only append an fps suffix above the common 30fps default; below/at 30 it's noise.
        if (variant.Fps is { } fps && fps > 30)
        {
            quality = string.Create(CultureInfo.InvariantCulture, $"{quality}{(int)Math.Round(fps)}");
        }

        string label = !string.IsNullOrWhiteSpace(variant.Codec)
            ? string.Create(CultureInfo.InvariantCulture, $"{quality} · {variant.Codec}")
            : quality;

        return variant.Bandwidth is { } bps && bps > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{label} · {bps / 1_000_000.0:0.0} Mbps")
            : label;
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
