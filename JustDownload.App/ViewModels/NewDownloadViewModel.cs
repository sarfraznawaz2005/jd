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
    private readonly ILogger<NewDownloadViewModel> _logger;

    // The user editing a field pins it, so re-detection never clobbers a manual choice.
    private bool _fileNameTouched;
    private bool _folderTouched;
    private long? _detectedSize;
    private CancellationTokenSource? _detectCts;

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

    public NewDownloadViewModel(
        IResourceProbe probe,
        IFileCategorizer categorizer,
        IDownloadFolderProvider folders,
        ISettingsService settings,
        IDownloadManager manager,
        IDownloadActions actions,
        ILogger<NewDownloadViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(categorizer);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(logger);
        _probe = probe;
        _categorizer = categorizer;
        _folders = folders;
        _settings = settings;
        _manager = manager;
        _actions = actions;
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

    /// <summary>Whether the form is complete and valid enough to enqueue.</summary>
    public bool CanSubmit =>
        TryGetValidUri(Url, out _)
        && !string.IsNullOrWhiteSpace(FileName) && FileNameError is null
        && !string.IsNullOrWhiteSpace(SaveToFolder) && FolderError is null;

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
        try
        {
            ResourceProbeResult result = await _probe.ProbeAsync(uri, headers: null, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || result is null)
            {
                return;
            }

            _detectedSize = result.TotalLength;
            ApplyDetection(result.SuggestedFileName, result.TotalLength, result.Resumable);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer detection — ignore.
        }
        catch (Exception ex) when (ex is ResourceProbeException or HttpRequestException or InvalidOperationException)
        {
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
        };

        long id = await _manager.EnqueueAsync(request).ConfigureAwait(true);
        if (startImmediately)
        {
            _actions.Start(id);
        }

        CloseRequested?.Invoke(this, true);
    }

    /// <summary>The effective category: the user's explicit pick, or auto-derived from the file name.</summary>
    private FileCategory ResolveCategory() =>
        SelectedCategory.Category ?? _categorizer.Categorize(FileName, contentType: null);

    /// <summary>Whether the current URL is a well-formed http(s) URL worth auto-detecting (the view triggers it).</summary>
    public bool CanDetect => TryGetValidUri(Url, out _);

    partial void OnFileNameChanged(string value) => _fileNameTouched = true;

    partial void OnSaveToFolderChanged(string value) => _folderTouched = true;

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
