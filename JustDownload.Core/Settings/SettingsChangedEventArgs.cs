namespace JustDownload.Core.Settings;

/// <summary>
/// Payload for <see cref="ISettingsService.Changed"/>, raised after one or more settings are updated
/// and persisted. Carries both the <see cref="Previous"/> and <see cref="Current"/> snapshots plus
/// the set of storage keys that actually changed, so a subscriber can react narrowly (e.g. only
/// re-theme the UI when <c>ui.theme</c> is in <see cref="ChangedKeys"/>).
/// </summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>Initializes the event payload.</summary>
    /// <param name="previous">The settings snapshot before the change.</param>
    /// <param name="current">The settings snapshot after the change.</param>
    /// <param name="changedKeys">The storage keys whose values changed.</param>
    public SettingsChangedEventArgs(
        AppSettings previous,
        AppSettings current,
        IReadOnlyList<string> changedKeys)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changedKeys);

        Previous = previous;
        Current = current;
        ChangedKeys = changedKeys;
    }

    /// <summary>The settings snapshot immediately before the change.</summary>
    public AppSettings Previous { get; }

    /// <summary>The settings snapshot immediately after the change.</summary>
    public AppSettings Current { get; }

    /// <summary>The storage keys whose values changed in this update (never empty).</summary>
    public IReadOnlyList<string> ChangedKeys { get; }
}
