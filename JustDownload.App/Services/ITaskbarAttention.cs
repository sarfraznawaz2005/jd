using Avalonia.Controls;

namespace JustDownload.App.Services;

/// <summary>
/// Requests the user's attention for a window without stealing focus (TASK-226) — the taskbar-button flash a
/// download manager uses to say "this finished" while the user is in another app.
/// </summary>
public interface ITaskbarAttention
{
    /// <summary>Whether this platform actually has an attention mechanism; <see langword="false"/> makes
    /// <see cref="Flash"/> an explicit no-op rather than a silent one.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Flashes <paramref name="window"/>'s taskbar button until the user brings it forward. Deliberately does
    /// not activate or raise the window — interrupting what the user is doing is exactly what this avoids.
    /// No-op when the window is already the foreground window (nothing to notice) or unsupported.
    /// </summary>
    void Flash(Window window);
}
