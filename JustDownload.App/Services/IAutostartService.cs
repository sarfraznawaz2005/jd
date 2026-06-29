namespace JustDownload.App.Services;

/// <summary>
/// Registers (or unregisters) the app to launch at OS login (TASK-122). The mechanism is per-OS; callers
/// check <see cref="IsSupported"/> before offering the option.
/// </summary>
public interface IAutostartService
{
    /// <summary>Whether launch-at-login is implemented on the current OS.</summary>
    bool IsSupported { get; }

    /// <summary>Whether the app is currently registered to launch at login.</summary>
    bool IsEnabled();

    /// <summary>Registers (<paramref name="enabled"/> true) or removes the launch-at-login entry. No-op when unsupported.</summary>
    void SetEnabled(bool enabled);
}
