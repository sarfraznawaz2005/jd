namespace JustDownload.Core.Settings;

/// <summary>
/// Typed, in-memory-cached settings store backed by the persistent settings repository
/// (TASK-020 / PRD §4.4 <c>settings</c> table). It exposes the current strongly-typed
/// <see cref="AppSettings"/> snapshot, applies typed updates that persist across restarts, and
/// raises <see cref="Changed"/> whenever a value actually changes.
/// <para>
/// Reads are served from the cached <see cref="Current"/> snapshot so the UI never blocks on disk;
/// call <see cref="LoadAsync"/> once at startup to hydrate it from storage.
/// </para>
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The current settings snapshot. Before <see cref="LoadAsync"/> runs (or when storage is empty)
    /// this is the all-defaults <see cref="AppSettings"/>. Always non-<see langword="null"/>.
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    /// Raised after an <see cref="UpdateAsync"/> that changed at least one value (and after the change
    /// has been persisted). Not raised for no-op updates or by <see cref="LoadAsync"/>.
    /// </summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>
    /// Hydrates <see cref="Current"/> from the persistent store, falling back to the typed default for
    /// any setting that is absent or stored in an unparseable form. Safe to call more than once.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a non-destructive update to the settings, persists only the keys that actually changed,
    /// updates <see cref="Current"/>, and raises <see cref="Changed"/> if anything changed.
    /// </summary>
    /// <param name="mutate">
    /// A pure transform from the current snapshot to the desired one, e.g.
    /// <c>s =&gt; s with { Theme = AppTheme.Dark }</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the persistence writes.</param>
    /// <returns>The resulting (possibly unchanged) settings snapshot.</returns>
    Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> mutate,
        CancellationToken cancellationToken = default);
}
