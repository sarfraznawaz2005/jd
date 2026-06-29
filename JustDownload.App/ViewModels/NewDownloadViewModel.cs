using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Formatting;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The "New URL" dialog (TASK-053, PRD 2.4.2): paste a URL and it auto-detects the file name, category, and
/// save folder by probing the resource, then enqueues the download (optionally starting it immediately). The
/// view-model holds all behaviour and validation so it is testable headless; the window is a thin shell that
/// binds to it and provides the folder picker. Depends only on interfaces (§6).
/// </summary>
public sealed partial class NewDownloadViewModel : ViewModelBase
{
    private readonly IResourceProbe _probe;
    private readonly IFileCategorizer _categorizer;
    private readonly IDownloadFolderProvider _folders;
    private readonly ISettingsService _settings;
    private readonly IDownloadManager _manager;
    private readonly IDownloadActions _actions;
    private readonly IDuplicateDownloadCheck _duplicateCheck;
    private readonly ILogger<NewDownloadViewModel> _logger;

    // The user editing a field pins it, so re-detection never clobbers a manual choice.
    private bool _fileNameTouched;
    private bool _folderTouched;
    private long? _detectedSize;
    private CancellationTokenSource? _detectCts;

    // Auth context from a browser-extension hand-off (TASK-091); applied to the enqueue, never shown.
    private string? _referrer;
    private string? _cookies;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    [NotifyPropertyChangedFor(nameof(UrlError))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    [NotifyPropertyChangedFor(nameof(FileNameError))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    [NotifyPropertyChangedFor(nameof(FolderError))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _saveToFolder = string.Empty;

    [ObservableProperty]
    private CategoryOption _selectedCategory;

    [ObservableProperty]
    private bool _useSegmentation = true;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private string? _detectionMessage;

    /// <summary>
    /// A prominent warning shown when a pre-queue HEAD probe found the link itself is bad — a 404/410/403 or an
    /// expired signed URL (TASK-142) — so the user catches a broken link before queuing. <see langword="null"/>
    /// when the link is fine or only a transient/network issue occurred (that uses <see cref="DetectionMessage"/>).
    /// </summary>
    [ObservableProperty]
    private string? _urlWarning;

    /// <summary>
    /// A warning shown when the target file already exists on disk or a download to the same destination is
    /// already in the library (TASK-139), so the user can rename or cancel (skip) rather than re-download or
    /// overwrite. <see langword="null"/> when there is no collision.
    /// </summary>
    [ObservableProperty]
    private string? _duplicateWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(ProxyOverrideError))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    private bool _useProxyOverride;

    [ObservableProperty]
    private ProxyKind _overrideProxyKind = ProxyKind.Http;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(ProxyOverrideError))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    private string _overrideProxyHost = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(ProxyOverrideError))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPausedCommand))]
    private int _overrideProxyPort;

    [ObservableProperty]
    private string _overrideProxyUsername = string.Empty;

    [ObservableProperty]
    private string _overrideProxyDomain = string.Empty;

    [ObservableProperty]
    private string _overrideProxyPassword = string.Empty;

    public NewDownloadViewModel(
        IResourceProbe probe,
        IFileCategorizer categorizer,
        IDownloadFolderProvider folders,
        ISettingsService settings,
        IDownloadManager manager,
        IDownloadActions actions,
        IDuplicateDownloadCheck duplicateCheck,
        ILogger<NewDownloadViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(categorizer);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(duplicateCheck);
        ArgumentNullException.ThrowIfNull(logger);
        _probe = probe;
        _categorizer = categorizer;
        _folders = folders;
        _settings = settings;
        _manager = manager;
        _actions = actions;
        _duplicateCheck = duplicateCheck;
        _logger = logger;

        Categories = BuildCategoryOptions();
        _selectedCategory = Categories[0]; // "Auto"
        _saveToFolder = folders.GetBaseFolder();
        ConnectionsHint = string.Create(
            CultureInfo.InvariantCulture,
            $"Use dynamic segmentation ({_settings.Current.ConnectionsPerDownload} connections)");
    }

    /// <summary>The category picker options: "Auto" plus every concrete category.</summary>
    public ObservableCollection<CategoryOption> Categories { get; }

    /// <summary>Caption for the segmentation toggle, reflecting the configured connection count.</summary>
    public string ConnectionsHint { get; }

    /// <summary>Raised when the dialog should close; <see langword="true"/> when a download was enqueued.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Validation message for the URL field, or <see langword="null"/> when valid.</summary>
    public string? UrlError =>
        string.IsNullOrWhiteSpace(Url) ? null // don't nag before anything is typed
        : TryGetValidUri(Url, out _) ? null
        : "Enter a valid http(s) URL.";

    /// <summary>Validation message for the file-name field, or <see langword="null"/> when valid.</summary>
    public string? FileNameError =>
        string.IsNullOrWhiteSpace(FileName) ? null
        : FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "The file name contains invalid characters."
        : null;

    /// <summary>Validation message for the folder field, or <see langword="null"/> when valid.</summary>
    public string? FolderError =>
        !string.IsNullOrWhiteSpace(SaveToFolder) && SaveToFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            ? "The folder path is invalid."
            : null;

    /// <summary>The proxy-override kind options (TASK-153); "None" is expressed by the on/off toggle instead.</summary>
    public IReadOnlyList<ProxyKind> ProxyKinds { get; } = [ProxyKind.Http, ProxyKind.Socks4, ProxyKind.Socks5];

    /// <summary>Validation for the per-download proxy override, or <see langword="null"/> when valid/unused.</summary>
    public string? ProxyOverrideError
    {
        get
        {
            if (!UseProxyOverride)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(OverrideProxyHost))
            {
                return "Enter the proxy host.";
            }

            return OverrideProxyPort is < 1 or > 65535 ? "Enter a port between 1 and 65535." : null;
        }
    }

    /// <summary>Whether the form is complete and valid enough to enqueue.</summary>
    public bool CanSubmit =>
        TryGetValidUri(Url, out _)
        && !string.IsNullOrWhiteSpace(FileName) && FileNameError is null
        && !string.IsNullOrWhiteSpace(SaveToFolder) && FolderError is null
        && ProxyOverrideError is null;

    /// <summary>
    /// Probes the current URL and fills the file name, category, and folder that the user has not overridden
    /// (AC0). Safe to call repeatedly; a newer call cancels an in-flight one. Network/probe failures are
    /// surfaced as a message and never throw to the caller — manual entry still works (D3, §1).
    /// </summary>
    public async Task DetectAsync()
    {
        if (!TryGetValidUri(Url, out Uri? uri))
        {
            return;
        }

        _detectCts?.Cancel();
        _detectCts?.Dispose();
        var cts = new CancellationTokenSource();
        _detectCts = cts;

        IsDetecting = true;
        DetectionMessage = null;
        UrlWarning = null;
        DuplicateWarning = null;
        try
        {
            ResourceProbeResult result = await _probe.ProbeAsync(uri, headers: null, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || result is null)
            {
                return;
            }

            _detectedSize = result.TotalLength;
            ApplyDetection(result.SuggestedFileName, result.TotalLength, result.Resumable);
            await CheckForDuplicateAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer detection — ignore.
        }
        catch (ResourceProbeException ex) when (ex.StatusCode > 0)
        {
            // The server answered, but the resource is bad (404/410/403/expiry): warn clearly before the user
            // queues a download that can't succeed (TASK-142).
            LogDetectFailed(_logger, uri, ex);
            UrlWarning = DescribeProbeFailure(ex.StatusCode);
        }
        catch (Exception ex)
        {
            // A transient/transport failure (timeout, DNS, parse, …): can't auto-read, but the link might still
            // be fine, so this is guidance rather than a hard warning (no silent failures, §1).
            LogDetectFailed(_logger, uri, ex);
            DetectionMessage = "Couldn't read this link automatically — check the URL or enter the details manually.";
        }
        finally
        {
            if (ReferenceEquals(_detectCts, cts))
            {
                IsDetecting = false;
                _detectCts = null;
            }

            cts.Dispose();
        }
    }

    private void ApplyDetection(string suggestedFileName, long? totalBytes, bool resumable)
    {
        if (!_fileNameTouched && !string.IsNullOrWhiteSpace(suggestedFileName))
        {
            SetFileNameQuietly(suggestedFileName);
        }

        // Category follows the (detected or chosen) file name; the folder follows the category unless pinned.
        FileCategory category = ResolveCategory();
        if (!_folderTouched)
        {
            SetFolderQuietly(_folders.GetFolderForCategory(category));
        }

        DetectionMessage = totalBytes is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ByteFormatter.FormatSize(totalBytes.Value)}{(resumable ? " · resumable" : " · no resume")}")
            : resumable ? "Size unknown · resumable" : "Size unknown · no resume";
    }

    /// <summary>
    /// Applies the auth context from a browser-extension hand-off (TASK-091) so the enqueued download carries
    /// the captured referrer and cookies. The cookies are passed to the engine (which stores them in the OS
    /// keychain) and are not displayed in the dialog.
    /// </summary>
    public void SetAuthContext(string? referrer, string? cookies)
    {
        _referrer = referrer;
        _cookies = cookies;
    }

    /// <summary>Enqueues the download and starts it immediately ("Download now").</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task DownloadNowAsync() => await SubmitAsync(startImmediately: true).ConfigureAwait(true);

    /// <summary>Enqueues the download but leaves it queued ("Add paused").</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task AddPausedAsync() => await SubmitAsync(startImmediately: false).ConfigureAwait(true);

    /// <summary>Cancels the dialog without enqueuing.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private async Task SubmitAsync(bool startImmediately)
    {
        if (!TryGetValidUri(Url, out Uri? uri))
        {
            return;
        }

        FileCategory category = ResolveCategory();
        var request = new EnqueueDownloadRequest
        {
            Url = uri,
            DestinationDirectory = SaveToFolder.Trim(),
            FileName = FileName.Trim(),
            TotalBytes = _detectedSize,
            CategoryType = category.ToString(),
            MaxConnections = UseSegmentation ? _settings.Current.ConnectionsPerDownload : 1,
            SpeedLimit = null,
            Referrer = _referrer,
            Cookies = _cookies,
            Proxy = BuildProxyOverride(),
        };

        long id = await _manager.EnqueueAsync(request).ConfigureAwait(true);
        if (startImmediately)
        {
            _actions.Start(id);
        }

        CloseRequested?.Invoke(this, true);
    }

    /// <summary>
    /// Builds the per-download proxy override (TASK-153) from the dialog fields, or <see langword="null"/> when
    /// the override is off (so the engine uses the global proxy). The password rides along as plaintext for the
    /// engine to store in the OS keychain (§5).
    /// </summary>
    private ProxyConfiguration? BuildProxyOverride()
    {
        if (!UseProxyOverride)
        {
            return null;
        }

        string? username = string.IsNullOrWhiteSpace(OverrideProxyUsername) ? null : OverrideProxyUsername.Trim();
        NetworkCredentials? credentials = username is null
            ? null
            : new NetworkCredentials(
                username,
                OverrideProxyPassword,
                string.IsNullOrWhiteSpace(OverrideProxyDomain) ? null : OverrideProxyDomain.Trim());

        return new ProxyConfiguration(OverrideProxyKind, OverrideProxyHost.Trim(), OverrideProxyPort, credentials);
    }

    /// <summary>The effective category: the user's explicit pick, or auto-derived from the file name.</summary>
    private FileCategory ResolveCategory() =>
        SelectedCategory.Category ?? _categorizer.Categorize(FileName, contentType: null);

    /// <summary>Whether the current URL is a well-formed http(s) URL worth auto-detecting (the view triggers it).</summary>
    public bool CanDetect => TryGetValidUri(Url, out _);

    /// <summary>
    /// Checks whether the chosen destination already holds this file (on disk or in the library) and sets
    /// <see cref="DuplicateWarning"/> accordingly (TASK-139). Best-effort: a check failure is swallowed so it
    /// never blocks the dialog.
    /// </summary>
    private async Task CheckForDuplicateAsync(CancellationToken cancellationToken)
    {
        try
        {
            DuplicateCheckResult result = await _duplicateCheck
                .CheckAsync(SaveToFolder.Trim(), FileName.Trim(), _detectedSize, cancellationToken)
                .ConfigureAwait(true);

            DuplicateWarning = result.Kind switch
            {
                DuplicateKind.FileExistsOnDisk when result.SizeMatches =>
                    "A file with this name and size already exists in this folder — it looks like the same file. "
                    + "Rename it, or cancel to skip.",
                DuplicateKind.FileExistsOnDisk =>
                    "A file with this name already exists in this folder and may be overwritten. Rename it, or cancel to skip.",
                DuplicateKind.AlreadyInLibrary =>
                    "You've already downloaded this file to this folder. Rename it, or cancel to skip.",
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer detection — ignore.
        }
    }

    /// <summary>Maps a probe failure status to a clear, human-readable warning (TASK-142).</summary>
    private static string DescribeProbeFailure(int statusCode) => statusCode switch
    {
        404 or 410 => $"This link looks broken — the server returned {statusCode} (not found). Check the URL before downloading.",
        401 or 403 => $"This link needs authorization — the server returned {statusCode}. It may be expired or require sign-in.",
        >= 500 => $"The server is having trouble — it returned {statusCode}. The download may fail; try again later.",
        _ => $"This link may not be downloadable — the server returned {statusCode}.",
    };

    // Editing the URL invalidates the previous probe's verdict until the next detect runs.
    partial void OnUrlChanged(string value) => UrlWarning = null;

    partial void OnFileNameChanged(string value)
    {
        _fileNameTouched = true;
        DuplicateWarning = null; // stale once the target name changes; re-checked on the next detect
    }

    partial void OnSaveToFolderChanged(string value)
    {
        _folderTouched = true;
        DuplicateWarning = null;
    }

    partial void OnSelectedCategoryChanged(CategoryOption value)
    {
        // An explicit category re-targets the folder (unless the user has pinned their own).
        if (!value.IsAuto && !_folderTouched)
        {
            SetFolderQuietly(_folders.GetFolderForCategory(value.Category!.Value));
        }
    }

    private void SetFileNameQuietly(string value)
    {
        bool wasTouched = _fileNameTouched;
        FileName = value;
        _fileNameTouched = wasTouched; // programmatic fill must not count as a manual edit
    }

    private void SetFolderQuietly(string value)
    {
        bool wasTouched = _folderTouched;
        SaveToFolder = value;
        _folderTouched = wasTouched;
    }

    private static ObservableCollection<CategoryOption> BuildCategoryOptions()
    {
        var options = new ObservableCollection<CategoryOption> { new("Auto-detect", null) };
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            if (category != FileCategory.Other)
            {
                options.Add(new CategoryOption(category.ToString(), category));
            }
        }

        options.Add(new CategoryOption("Other", FileCategory.Other));
        return options;
    }

    private static bool TryGetValidUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Auto-detection failed for {Url}.")]
    private static partial void LogDetectFailed(ILogger logger, Uri url, Exception exception);
}
