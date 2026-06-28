using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Logging;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Dash;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Storage;
using JustDownload.Core.Throttling;
using JustDownload.Core.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JustDownload.Core;

/// <summary>
/// The single composition root for the headless engine (architecture §6 "DI everywhere").
/// Every front-end — <c>JustDownload.App</c>, <c>JustDownload.NativeHost</c>, the test suite,
/// and a future CLI — builds its <see cref="IServiceProvider"/> from this one registration so
/// Core services are wired identically and stay reusable.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the JustDownload.Core services behind their interfaces. Uses <c>TryAdd</c> so a
    /// caller (notably a test) can pre-register a substitute and have it win. This also wires the
    /// logging pipeline (TASK-016): a configurable minimum level and a secret-redacting logger
    /// factory, plus the global error handler.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="configureLogging">
    /// Optional hook to set the log level (and future logging options). When <see langword="null"/>,
    /// the defaults from <see cref="LoggingOptions"/> apply.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadCore(
        this IServiceCollection services,
        Action<LoggingOptions>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAppInfoProvider, AppInfoProvider>();

        services.AddJustDownloadLogging(configureLogging);
        services.AddJustDownloadData();
        services.AddJustDownloadSecrets();
        services.AddJustDownloadCategorization();
        services.AddJustDownloadTransport();
        services.AddJustDownloadStorage();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        services.AddJustDownloadMedia();
        services.AddJustDownloadNativeMessaging();

        // Typed settings store over the settings repository (TASK-021): sane defaults, change
        // notifications, and persistence across restarts. Singleton so the cached snapshot and the
        // Changed event are shared by every consumer.
        services.TryAddSingleton<ISettingsService, SettingsService>();

        return services;
    }

    /// <summary>
    /// Registers the engine's logging + global error handling (TASK-016): the secret redactor, a
    /// redacting <see cref="ILoggerFactory"/> decorator so every logger masks secrets, the
    /// configurable minimum level, and <see cref="IGlobalErrorHandler"/>. Idempotent and safe to
    /// call alongside a host's own <c>AddLogging</c> (which can add sinks like console/debug).
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="configureLogging">Optional logging configuration hook.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadLogging(
        this IServiceCollection services,
        Action<LoggingOptions>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LoggingOptions();
        configureLogging?.Invoke(options);

        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();

        // Build the standard logging infrastructure (filter options, provider plumbing) and apply
        // the configurable minimum level.
        services.AddLogging(builder => builder.SetMinimumLevel(options.MinimumLevel));

        // Decorate ILoggerFactory so every logger redacts secrets before reaching any provider.
        // Registered last so it wins resolution; Logger<T> (the ILogger<> implementation) then
        // pulls this factory. The inner LoggerFactory is reconstructed from the same DI-registered
        // providers and filter options the default factory would have used.
        services.AddSingleton<ILoggerFactory>(sp =>
        {
            var inner = new LoggerFactory(
                sp.GetServices<ILoggerProvider>(),
                sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());

            return new RedactingLoggerFactory(inner, sp.GetRequiredService<ISecretRedactor>());
        });

        services.TryAddSingleton<IGlobalErrorHandler, GlobalErrorHandler>();

        return services;
    }

    /// <summary>
    /// Registers the SQLite data layer (TASK-018, LOCKED DECISION D6): per-OS database path
    /// resolution and a connection factory that opens connections with WAL journaling and a busy
    /// timeout for safe concurrent read/write. Uses <c>TryAdd</c> so a test can substitute a
    /// temp-path provider or options before this runs.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new DatabaseOptions());
        services.TryAddSingleton<IDatabasePathProvider, DatabasePathProvider>();
        services.TryAddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();

        // Versioned schema migrations (TASK-019). Migrations are contributed as an ordered set so
        // later tasks append new ones without touching the runner; the runner applies any whose
        // version exceeds the database's current user_version, idempotently.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, InitialSchemaMigration>());
        services.TryAddSingleton<IMigrationRunner, MigrationRunner>();

        // Repositories (TASK-020) — the centralized data-access seam. Stateless over the shared
        // connection factory, so a singleton lifetime is correct and cheap. All SQL for downloads,
        // segments, settings, and the site blacklist lives behind these interfaces (architecture §6
        // "centralize DB access"; no raw SQL elsewhere).
        services.TryAddSingleton<IDownloadRepository, DownloadRepository>();
        services.TryAddSingleton<ISegmentRepository, SegmentRepository>();
        services.TryAddSingleton<ISettingsRepository, SettingsRepository>();
        services.TryAddSingleton<IBlacklistRepository, BlacklistRepository>();

        return services;
    }

    /// <summary>
    /// Registers the OS keychain secret store (TASK-022, CLAUDE.md §5 / PRD §4.6): credentials and
    /// tokens live in the per-user OS vault — DPAPI on Windows, the login Keychain on macOS, the
    /// Secret Service / libsecret on Linux — and only the opaque <c>secret_ref</c> is ever persisted
    /// in SQLite. The concrete backend is chosen by the host OS; an unsupported platform gets a
    /// store that fails loudly rather than falling back to plaintext. Uses <c>TryAdd</c> so a test
    /// can substitute an in-memory store.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadSecrets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISecretStorePathProvider, SecretStorePathProvider>();

        // Each branch is guarded by the matching OperatingSystem check so the platform-availability
        // analyzer (CA1416) can prove the OS-specific store type is only referenced on its OS.
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretStore, WindowsDpapiSecretStore>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<ISecretStore, MacOsKeychainSecretStore>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.TryAddSingleton<ISecretStore, LinuxSecretToolSecretStore>();
        }
        else
        {
            services.TryAddSingleton<ISecretStore, UnsupportedSecretStore>();
        }

        return services;
    }

    /// <summary>
    /// Registers file-type categorization (TASK-044, PRD US-8): the user-editable rule set seeded with
    /// the product's comprehensive extension/MIME defaults, and the pure <see cref="IFileCategorizer"/>
    /// that resolves a file into one of the PRD categories from its extension and/or content type. Both
    /// are singletons (categorization is stateless); the shared <see cref="CategorizationRules"/> is
    /// registered as a concrete so a host can resolve and edit it at runtime. Uses <c>TryAdd</c> so a
    /// test or host can pre-register its own rules or categorizer and have it win.
    /// <para>
    /// Also registers persistence of the user's rule overrides (TASK-085): the settings-backed
    /// <see cref="ICategoryRuleStore"/> and the <see cref="ICategoryRuleService"/> that applies persisted
    /// overrides over the defaults at startup and saves new edits. (Persistence depends on the data layer,
    /// which <see cref="AddJustDownloadCore"/> registers before this.)
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadCategorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CategorizationRules.CreateDefault());
        services.TryAddSingleton<IFileCategorizer, FileCategorizer>();

        services.TryAddSingleton<ICategoryRuleStore, SettingsCategoryRuleStore>();
        services.TryAddSingleton<ICategoryRuleService, CategoryRuleService>();

        // Move-completed-downloads-by-category (TASK-046): editable folder-name rules + the organizer.
        services.TryAddSingleton(CategoryFolderRules.CreateDefault());
        services.TryAddSingleton<IDownloadOrganizer, DownloadOrganizer>();

        return services;
    }

    /// <summary>
    /// Registers the HTTP/HTTPS transport (TASK-023): the single shared <see cref="SocketsHttpHandler"/>
    /// (one connection pool for the whole app, AC2) and the <see cref="ITransport"/> over it. Both are
    /// singletons. Uses <c>TryAdd</c> so a test can substitute a fake transport, and registers a default
    /// <see cref="TransportOptions"/> a host can pre-register to override.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new TransportOptions());
        services.TryAddSingleton<ISharedHttpHandlerProvider, SharedHttpHandlerProvider>();
        services.TryAddSingleton<ITransport, HttpTransport>();

        // Range-capability probe (TASK-024): decides segmented vs single-connection downloads.
        services.TryAddSingleton<IResourceProbe, ResourceProbe>();

        return services;
    }

    /// <summary>
    /// Registers the download storage layer (TASK-025): the segment checkpointer that persists per-
    /// segment offsets as the resume checkpoint. Transient because one instance coalesces the segments of
    /// a single download — the engine resolves one per active download. The pre-allocated output file
    /// (<see cref="PreallocatedFile"/>) is created on demand by the engine, not from the container.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddTransient<ISegmentCheckpointer, SegmentCheckpointer>();

        return services;
    }

    /// <summary>
    /// Registers dynamic segmentation (TASK-026): the segmented downloader that splits a resource across
    /// connections with work-stealing, plus the default <see cref="SegmentationOptions"/> (a host can
    /// pre-register its own to override). Depends on the transport (probe + range GET) and storage layers.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadDownloading(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The engine needs a clock and the shared global bandwidth cap (TASK-030); TryAdd so the full
        // composition root (which registers IClock) and a host's own global limiter still win.
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IRateLimiter>(sp => new TokenBucket(sp.GetRequiredService<IClock>()));

        services.TryAddSingleton(new SegmentationOptions());
        services.TryAddSingleton<ISegmentedDownloader, SegmentedDownloader>();

        return services;
    }

    /// <summary>
    /// Registers the download lifecycle manager (TASK-031): the queue/state-machine orchestrator that drives
    /// downloads through queued → active → complete/error, persists every transition, and raises observable
    /// status/progress events the UI binds to (US-15b). Singleton so its in-session progress cache and events
    /// are shared by every consumer. Depends on the data layer (download repository) and the segmentation
    /// engine, both registered before this in <see cref="AddJustDownloadCore"/>.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDownloadManager, DownloadManager>();

        // Live Completed/Incomplete grouping for the list/sidebar (TASK-045, US-8). Singleton so it holds
        // one shared membership view and subscribes to the single manager's status events.
        services.TryAddSingleton<IDownloadStatusGroups, DownloadStatusGroupTracker>();

        // Startup crash-recovery scan (TASK-029): demote downloads left active by an unclean shutdown to
        // resumable. Run once via InitializeJustDownloadCoreAsync.
        services.TryAddSingleton<IDownloadRecovery, DownloadRecoveryService>();

        return services;
    }

    /// <summary>
    /// Registers the ffmpeg integration (TASK-040, D7): options, the locator (path + version check), and
    /// the process runner used by HLS concat / A-V mux / ts→mp4 remux. ffmpeg is invoked as a separate
    /// LGPL process and is never required at startup — it is resolved lazily on first media use.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadMedia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new FfmpegOptions());
        services.TryAddSingleton<IFfmpegLocator, FfmpegLocator>();
        services.TryAddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.TryAddSingleton<IMediaConverter, MediaConverter>();

        // Pluggable extractor registry (TASK-036, D3): generic extractors register at startup and are tried
        // in priority order; the registry degrades gracefully when nothing recognises a URL. Specific
        // extractors (HLS/DASH) are appended by their own tasks.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMediaExtractor, ProgressiveMediaExtractor>());

        // HLS extractor + segment downloader (TASK-037): parses master/media .m3u8, downloads segments in
        // parallel and decrypts AES-128. Registered before the generic extractor via its lower Priority.
        services.TryAddSingleton(new HlsOptions());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, HlsMediaExtractor>());
        services.TryAddSingleton<IHlsDownloader, HlsDownloader>();

        // HLS concat (TASK-038): byte-exact append of downloaded segments into one .ts.
        services.TryAddSingleton<IHlsConcatenator, HlsConcatenator>();

        // DASH / separate video+audio streams (TASK-039): the .mpd extractor plus the concurrent
        // two-stream downloader (each stream segmented, with its own progress and resume).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, DashMediaExtractor>());
        services.TryAddSingleton<ISeparateStreamDownloader, SeparateStreamDownloader>();

        // A/V mux (TASK-041): stream-copy the two streams into one container (MKV default, MP4 when codecs allow).
        services.TryAddSingleton<IMediaMuxer, MediaMuxer>();

        services.TryAddSingleton<IMediaExtractorRegistry, MediaExtractorRegistry>();

        return services;
    }

    /// <summary>
    /// Registers the Native Messaging Host (TASK-064, D8): options (the extension allowlist), the message
    /// handler, and the stdio host loop. The browser extension talks to the app through this — over
    /// stdin/stdout, with no listening socket.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadNativeMessaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new NativeHostOptions());
        services.TryAddSingleton<INativeMessageHandler, PingNativeMessageHandler>();
        services.TryAddSingleton<NativeMessageHost>();

        return services;
    }
}
