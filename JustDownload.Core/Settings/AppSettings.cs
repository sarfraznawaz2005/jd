namespace JustDownload.Core.Settings;

/// <summary>
/// The immutable, strongly-typed snapshot of all user-configurable application settings, with the
/// product's sane defaults baked into the initializers (TASK-021). Being a <c>record</c> makes
/// non-destructive mutation (<c>settings with { Theme = AppTheme.Dark }</c>) the natural way to
/// change one value while keeping the rest, and gives value equality for free so the settings
/// service can detect whether anything actually changed before persisting or notifying.
/// <para>
/// This type holds only non-secret preferences; credentials and tokens never live here (they go to
/// the OS keychain — CLAUDE.md §5).
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>Maximum number of downloads running at once. Default <c>4</c>.</summary>
    public int MaxConcurrentDownloads { get; init; } = 4;

    /// <summary>Maximum parallel connections (segments) per download. Default <c>8</c>.</summary>
    public int ConnectionsPerDownload { get; init; } = 8;

    /// <summary>
    /// Global download speed cap in bytes per second. <c>0</c> means unlimited (the default).
    /// </summary>
    public long GlobalSpeedLimitBytesPerSecond { get; init; }

    /// <summary>The preferred default video resolution. Default <see cref="VideoQuality.P1080"/>.</summary>
    public VideoQuality DefaultVideoQuality { get; init; } = VideoQuality.P1080;

    /// <summary>The preferred default output container. Default <see cref="MediaContainer.Mkv"/>.</summary>
    public MediaContainer DefaultContainer { get; init; } = MediaContainer.Mkv;

    /// <summary>The list/layout density. Default <see cref="UiDensity.Comfortable"/>.</summary>
    public UiDensity Density { get; init; } = UiDensity.Comfortable;

    /// <summary>The visual theme. Default <see cref="AppTheme.Light"/> (locked decision D4).</summary>
    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>
    /// Whether completed downloads are moved into a per-category subfolder (US-8 AC3, TASK-046).
    /// Default <see langword="false"/> — the file stays where it was downloaded.
    /// </summary>
    public bool OrganizeByCategory { get; init; }

    /// <summary>
    /// The root directory under which category subfolders are created when
    /// <see cref="OrganizeByCategory"/> is on. <see langword="null"/>/empty means "use the directory the
    /// file was downloaded to" (organize in place). Default <see langword="null"/>.
    /// </summary>
    public string? OrganizedRootDirectory { get; init; }

    /// <summary>
    /// Whether the app launches hidden to the system tray (no window, no taskbar entry) instead of
    /// showing the main window. The tray icon restores it. Default <see langword="false"/>.
    /// </summary>
    public bool StartMinimizedToTray { get; init; }

    /// <summary>
    /// Whether closing the main window (the title-bar X) hides it to the system tray instead of quitting;
    /// the app keeps running and only the tray "Quit" exits. Default <see langword="false"/>.
    /// </summary>
    public bool CloseToTray { get; init; }
}
