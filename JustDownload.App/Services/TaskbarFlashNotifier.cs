using Avalonia.Controls;
using Avalonia.Threading;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.Services;

/// <summary>
/// Flashes the taskbar when a download completes (TASK-226), so a user working in another app notices without
/// being interrupted. Which window flashes is resolved per download by the shell — the download's own progress
/// window (TASK-225) when one is open, otherwise the main window — keeping this class free of window lookup
/// and unit-testable on its own (§3).
/// </summary>
public sealed class TaskbarFlashNotifier : IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly ITaskbarAttention _attention;
    private readonly Func<long, Window?> _resolveTarget;
    private bool _disposed;

    public TaskbarFlashNotifier(
        IDownloadManager manager, ITaskbarAttention attention, Func<long, Window?> resolveTarget)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentNullException.ThrowIfNull(resolveTarget);
        _manager = manager;
        _attention = attention;
        _resolveTarget = resolveTarget;
    }

    /// <summary>Begins listening for completions.</summary>
    public void Start() => _manager.StatusChanged += OnStatusChanged;

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.Current != DownloadStatus.Completed || !_attention.IsSupported)
        {
            return;
        }

        // The manager raises this off the UI thread; window handles must be touched on it.
        Dispatcher.UIThread.Post(() => FlashFor(e.DownloadId));
    }

    /// <summary>Flashes the window that represents a download, if there is one. Public so tests can drive it.</summary>
    public void FlashFor(long downloadId)
    {
        if (!_disposed && _resolveTarget(downloadId) is { } window)
        {
            _attention.Flash(window);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;
    }
}
