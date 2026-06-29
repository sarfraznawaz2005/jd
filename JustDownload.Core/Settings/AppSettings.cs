using JustDownload.Core.Transport.Proxy;

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
    /// How many times a download auto-retries a transient (network) failure before giving up, with
    /// exponential backoff between attempts (TASK-131). <c>0</c> disables auto-retry. Default <c>5</c>.
    /// </summary>
    public int MaxDownloadRetries { get; init; } = 5;

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
    /// The default folder new downloads save into (TASK-121). <see langword="null"/>/empty means "use the
    /// OS Downloads folder". Default <see langword="null"/>.
    /// </summary>
    public string? DefaultDownloadDirectory { get; init; }

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

    /// <summary>
    /// Whether the app watches the clipboard and offers to download a copied supported URL (TASK-133).
    /// Opt-in — default <see langword="false"/>.
    /// </summary>
    public bool MonitorClipboard { get; init; }

    /// <summary>Whether the app is registered to launch at OS login (TASK-122). Opt-in — default <see langword="false"/>.</summary>
    public bool LaunchAtStartup { get; init; }

    /// <summary>
    /// Whether desktop notifications are shown when a download finishes or fails (TASK-123). On by default;
    /// turn off to silence completion/error toasts.
    /// </summary>
    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>The global proxy kind applied to downloads (TASK-125). Default <see cref="ProxyKind.None"/>.</summary>
    public ProxyKind ProxyKind { get; init; } = ProxyKind.None;

    /// <summary>The proxy host (TASK-125). <see langword="null"/>/empty disables the proxy. Default <see langword="null"/>.</summary>
    public string? ProxyHost { get; init; }

    /// <summary>The proxy port (TASK-125). Default <c>0</c>.</summary>
    public int ProxyPort { get; init; }

    /// <summary>The proxy auth user name, or <see langword="null"/> for an unauthenticated proxy (TASK-125).</summary>
    public string? ProxyUsername { get; init; }

    /// <summary>The NTLM/Negotiate domain for proxy auth, or <see langword="null"/> (TASK-125).</summary>
    public string? ProxyDomain { get; init; }

    /// <summary>
    /// Opaque OS-keychain reference (§5) for the proxy auth password — never the password itself. Resolved
    /// on demand when the proxy is applied (TASK-125). <see langword="null"/> when the proxy has no password.
    /// </summary>
    public string? ProxyPasswordSecretRef { get; init; }
}
