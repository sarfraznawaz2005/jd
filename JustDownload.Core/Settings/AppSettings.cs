using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Whether a completed archive (<c>.zip</c>) is automatically extracted into a sibling folder
    /// (TASK-135). Opt-in — default <see langword="false"/>.
    /// </summary>
    public bool AutoExtractArchives { get; init; }

    /// <summary>
    /// Per-category concurrent-download caps (TASK-141) in the canonical form <c>Video=2;Compressed=1</c>
    /// (parsed by <c>CategoryConcurrency</c>). A category absent from the list is uncapped (bounded only by
    /// the global <see cref="MaxConcurrentDownloads"/>). <see langword="null"/>/empty means no per-category
    /// caps — the default.
    /// </summary>
    public string? CategoryConcurrencyLimits { get; init; }

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

    /// <summary>
    /// The minimum log level the app emits (TASK-127, Advanced settings). Applied live through the logging
    /// switch and persisted across restarts. Default <see cref="LogLevel.Information"/>.
    /// </summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// An optional program to run when a download completes (TASK-136); the completed file's full path is
    /// passed to it as a single argument (no shell, so the path is passed safely). <see langword="null"/>/empty
    /// disables the hook — the default.
    /// </summary>
    public string? OnCompletionCommand { get; init; }

    /// <summary>
    /// Time-of-day bandwidth rules (TASK-145) in the canonical form <c>HH:mm-HH:mm=bytes;…</c> (parsed by
    /// <c>BandwidthSchedule</c>). When a rule is active its cap overrides <see cref="GlobalSpeedLimitBytesPerSecond"/>;
    /// otherwise the manual cap applies. <see langword="null"/>/empty = no schedule (the default).
    /// </summary>
    public string? BandwidthSchedule { get; init; }

    /// <summary>
    /// Whether the one-time "may violate site ToS" notice (docs/LEGAL.md, TASK-160) has been dismissed with
    /// "Don't show this again" and should stay suppressed on future media downloads. Default
    /// <see langword="false"/> — the notice is shown before the first media download.
    /// </summary>
    public bool SuppressTosNotice { get; init; }
}
