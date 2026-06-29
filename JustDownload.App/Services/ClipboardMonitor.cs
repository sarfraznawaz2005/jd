using Avalonia.Threading;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Opt-in clipboard watcher (TASK-133): when <see cref="AppSettings.MonitorClipboard"/> is on, it polls the
/// clipboard and raises <see cref="UrlDetected"/> the first time a newly-copied supported URL appears, so the
/// shell can offer to download it. Off by default; the app's own copies (via <see cref="IClipboardService"/>)
/// and repeats of the same text are ignored. Enable/disable follows the setting live; the poll itself runs on
/// the UI thread (clipboard access requires it).
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _settings;
    private DispatcherTimer? _timer;
    private string? _lastSeen;
    private bool _disposed;

    public ClipboardMonitor(IClipboardService clipboard, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(settings);
        _clipboard = clipboard;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised with a supported URL freshly copied to the clipboard while monitoring is enabled.</summary>
    public event EventHandler<string>? UrlDetected;

    /// <summary>Whether the poll timer is currently running.</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Starts or stops the watcher to match the current setting. Call once after settings load.</summary>
    public void ApplyEnabled()
    {
        if (_settings.Current.MonitorClipboard)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Reads the clipboard once and raises <see cref="UrlDetected"/> for a newly-seen supported URL. Returns
    /// whether it raised. Skips empty text, repeats, and the app's own last copy. Exposed for testing.
    /// </summary>
    public async Task<bool> PollOnceAsync()
    {
        string? text = await _clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text) || text == _lastSeen)
        {
            return false;
        }

        _lastSeen = text;
        if (text == _clipboard.LastCopiedText)
        {
            return false; // this app put it there — don't offer to re-download it
        }

        string? url = DroppedLinkParser.TryExtractUrl(text);
        if (url is null)
        {
            return false;
        }

        UrlDetected?.Invoke(this, url);
        return true;
    }

    private void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        // Seed from the current clipboard so existing content isn't offered the moment monitoring turns on.
        _ = SeedAsync();
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer = null;
        _lastSeen = null;
    }

    private async Task SeedAsync() => _lastSeen = await _clipboard.GetTextAsync().ConfigureAwait(true);

    private async void OnTick(object? sender, EventArgs e) => await PollOnceAsync().ConfigureAwait(true);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => ApplyEnabled();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        Stop();
    }
}
