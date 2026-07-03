using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.Downloading;
using JustDownload.Core.Integrity;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Logging;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Dash;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Facebook;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;
using JustDownload.Core.Media.YouTube;
using JustDownload.Core.Media.YtDlp;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.NativeMessaging.Registration;
using JustDownload.Core.PostProcess;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Throttling;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Ftp;
using JustDownload.Core.Transport.Proxy;
using JustDownload.Core.Updates;
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
        services.TryAddSingleton<IAppVersionProvider, AppVersionProvider>();

        // Post-download integrity check against a user/page-supplied MD5/SHA-256 hash (TASK-132). Stateless.
        services.TryAddSingleton<IChecksumVerifier, ChecksumVerifier>();

        // Post-download archive extraction (TASK-135). Stateless.
        services.TryAddSingleton<IArchiveExtractor, ArchiveExtractor>();

        services.AddJustDownloadLogging(configureLogging);
        services.AddJustDownloadData();
        services.AddJustDownloadSecrets();
        services.AddJustDownloadCategorization();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        services.AddJustDownloadMedia();
        services.AddJustDownloadNativeMessaging();
        services.AddJustDownloadUpdates();

        // Typed settings store over the settings repository (TASK-021): sane defaults, change
        // notifications, and persistence across restarts. Singleton so the cached snapshot and the
        // Changed event are shared by every consumer.
        services.TryAddSingleton<ISettingsService, SettingsService>();

        // Backup/migrate preferences to and from a portable JSON file (TASK-129).
        services.TryAddSingleton<ISettingsTransfer, SettingsTransfer>();

        // Bridges the persisted global speed limit to the shared rate limiter (US-3). Singleton so its
        // Changed subscription lives for the app; the host calls ApplyCurrent() once after settings load.
        services.TryAddSingleton<GlobalSpeedLimitController>();

        // Bridges the persisted global proxy (TASK-125) to the engine's proxy service; the host calls
        // ApplyCurrentAsync() once after settings load. Singleton so its Changed subscription lives.
        services.TryAddSingleton<GlobalProxyController>();

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

        // A runtime-mutable minimum-level authority (TASK-127), seeded from the configured level. The inner
        // logger is left wide-open (Trace) so the switch — consulted by every RedactingLogger — can raise or
        // lower verbosity live from settings without rebuilding the factory.
        services.TryAddSingleton<ILogLevelSwitch>(new LogLevelSwitch(options.MinimumLevel));
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));

        // A real sink for Error/Critical (TASK-179): before this, nothing was registered as an
        // ILoggerProvider at all, so even IGlobalErrorHandler's captures were logged into a pipeline with
        // no destination. Internal diagnostic trail only — no Settings UI, no verbosity control. Every host
        // (App, NativeHost, Cli) gets this automatically.
        services.AddSingleton<ILoggerProvider, ErrorLogFileProvider>();

        // Decorate ILoggerFactory so every logger redacts secrets before reaching any provider.
        // Registered last so it wins resolution; Logger<T> (the ILogger<> implementation) then
        // pulls this factory. The inner LoggerFactory is reconstructed from the same DI-registered
        // providers and filter options the default factory would have used.
        services.AddSingleton<ILoggerFactory>(sp =>
        {
            var inner = new LoggerFactory(
                sp.GetServices<ILoggerProvider>(),
                sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());

            return new RedactingLoggerFactory(
                inner, sp.GetRequiredService<ISecretRedactor>(), sp.GetRequiredService<ILogLevelSwitch>());
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
        services.TryAddSingleton<IPortableEnvironment, PortableEnvironment>();
        services.TryAddSingleton<IDatabasePathProvider, DatabasePathProvider>();
        services.TryAddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();

        // Versioned schema migrations (TASK-019). Migrations are contributed as an ordered set so
        // later tasks append new ones without touching the runner; the runner applies any whose
        // version exceeds the database's current user_version, idempotently.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, InitialSchemaMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadPriorityMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadCookieSecretRefMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadRetryCountMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadProxyOverrideMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadMediaKindMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadMediaStreamsMigration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigration, AddDownloadAlternateUrlsMigration>());
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

        // SecretStorePathProvider needs app info for its vault path; ensure it's available so this module is
        // self-contained (idempotent — the composition root registers it too).
        services.TryAddSingleton<IAppInfoProvider, AppInfoProvider>();
        services.TryAddSingleton<ISecretStorePathProvider, SecretStorePathProvider>();

        // Each branch is guarded by the matching OperatingSystem check so the platform-availability
        // analyzer (CA1416) can prove the OS-specific store type is only referenced on its OS.
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretStore, WindowsDpapiSecretStore>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<IMacKeychainInterop, MacKeychainInterop>();
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

        // Credential persistence (TASK-035 AC1): passwords are stored only via the OS keychain; the DB keeps
        // just the opaque secret_ref plus the non-secret username/domain.
        services.TryAddSingleton<ICredentialStore, CredentialStore>();

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

        // Proxy support (TASK-034, US-6): the runtime-toggleable global/per-download proxy resolver and the
        // profile-keyed HTTP client pool (direct over the shared handler; one pooled handler per proxy/cred).
        services.TryAddSingleton<IProxyService, ProxyService>();
        services.TryAddSingleton<IHttpClientProvider, HttpClientProvider>();

        // Proxy connectivity test for the settings panel (TASK-152): one user-initiated probe through the
        // entered proxy config, reporting reachable / 407 / unreachable.
        services.TryAddSingleton<IProxyTester, ProxyTester>();

        // HTTP/proxy authentication (TASK-035, US-7): the per-download credential flow. .NET answers
        // Basic/Digest/NTLM challenges from the credentials carried on the pooled handler.
        services.TryAddSingleton<ICredentialContext, CredentialContext>();

        // Scheme-routed transport (TASK-033): HTTP(S) → HttpTransport, FTP(S) → FtpTransport. The concrete
        // transports are registered so the router can compose them; the engine depends only on ITransport.
        services.TryAddSingleton<HttpTransport>();
        services.TryAddSingleton<IFtpConnectionFactory, FluentFtpConnectionFactory>();
        services.TryAddSingleton<FtpTransport>();
        services.TryAddSingleton<ITransport, SchemeRoutingTransport>();

        // Range-capability probe (TASK-024): decides segmented vs single-connection downloads.
        services.TryAddSingleton<IResourceProbe, ResourceProbe>();

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

        // The manager resolves cookies from the OS keychain (TASK-091), so ensure the secret store is
        // registered wherever lifecycle is (idempotent — full AddJustDownloadCore already adds it).
        services.AddJustDownloadSecrets();

        // The manager routes media-variant downloads through the media coordinator (TASK-154), so ensure the
        // media services are registered wherever lifecycle is (idempotent — full AddJustDownloadCore adds them).
        services.AddJustDownloadMedia();

        // The queue and the retry policy both read settings (max concurrency, max retries), so ensure the
        // settings service is registered wherever lifecycle is (idempotent — full AddJustDownloadCore adds it).
        services.TryAddSingleton<ISettingsService, SettingsService>();

        // Auto-retry policy for transient (network) failures (TASK-131): exponential backoff, retry count
        // from settings. Singleton — stateless over the settings snapshot.
        services.TryAddSingleton<IRetryBackoff, ExponentialBackoff>();

        services.TryAddSingleton<IDownloadManager, DownloadManager>();

        // Pre-download duplicate detection for the New URL dialog (TASK-139).
        services.TryAddSingleton<IDuplicateDownloadCheck, DuplicateDownloadCheck>();

        // View/remove saved keychain credentials for the Authentication settings (TASK-126).
        services.TryAddSingleton<Security.ISavedCredentialsService, Security.SavedCredentialsService>();

        // Import a URL list / export the queue as M3U/CSV/JSON (TASK-140).
        services.TryAddSingleton<IDownloadListTransfer, DownloadListTransfer>();

        // Download queue (TASK-072, US-16): enforces the max-concurrent limit and priority order, starting
        // queued downloads through the manager as slots free up. Singleton so one queue owns scheduling.
        services.TryAddSingleton<IDownloadQueueService, DownloadQueueService>();

        // Scheduler (TASK-073, US-16): timed start/stop of the queue and an opt-in shutdown/sleep when the
        // queue drains. The power controller is only reached on explicit user opt-in.
        services.TryAddSingleton<ISystemPowerController, SystemPowerController>();
        services.TryAddSingleton<IDownloadScheduler, DownloadScheduler>();

        // Batch add (TASK-074, US-16): expand pasted URLs (incl. [a-b] patterns) and enqueue them.
        services.TryAddSingleton<IBatchEnqueuer, BatchEnqueuer>();

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

        // The yt-dlp fallback extractor (TASK-163) gates on ISettingsService.Current.VideoCaptureEnabled, so
        // ensure the settings service is registered wherever media is (idempotent — the full composition
        // root already adds it via AddJustDownloadLifecycle before this).
        services.TryAddSingleton<ISettingsService, SettingsService>();

        // FfmpegOptions/YtDlpOptions' vendor-directory defaults (TASK-186, below) need this; ensure it's
        // registered wherever media is, same reasoning as ISettingsService just above.
        services.TryAddSingleton<IAppInfoProvider, AppInfoProvider>();

        // VendorDirectory gets a real default here (TASK-186) rather than staying null until
        // FfmpegProvisioner sets it as a side effect of a successful download: that mutation only lives in
        // this process's memory, so a fresh restart forgot it ever ran and the locator's vendor-directory
        // candidate was skipped entirely — a provisioned ffmpeg silently "disappeared" on every restart.
        services.TryAddSingleton(sp => new FfmpegOptions
        {
            VendorDirectory = Path.Combine(AppDataPaths.Directory(sp.GetRequiredService<IAppInfoProvider>()), "ffmpeg"),
        });
        services.TryAddSingleton<IFfmpegLocator, FfmpegLocator>();
        services.TryAddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.TryAddSingleton<IMediaConverter, MediaConverter>();

        // Download-on-first-use of a pinned LGPL ffmpeg when the system has none (TASK-079, D7): the
        // manifest of LGPL builds and the provisioner that fetches, integrity-checks, and extracts one.
        services.TryAddSingleton(FfmpegManifest.Default);
        services.TryAddSingleton<IFfmpegProvisioner, FfmpegProvisioner>();

        // Optional yt-dlp fallback (TASK-162/163, D3): registered unconditionally, same as ffmpeg above, but
        // nothing is ever fetched or invoked unless the user opts in via AppSettings.VideoCaptureEnabled,
        // explicitly downloads it, and every in-house extractor (below) has already declined. Never bundled
        // or statically linked — downloaded on demand and run as a separate process, exactly like ffmpeg (D7).
        // Same fix, same reason as FfmpegOptions above (TASK-186): a real default instead of null-until-a-
        // successful-provision, so the locator can find a previously-downloaded yt-dlp after a restart.
        services.TryAddSingleton(sp => new YtDlpOptions
        {
            VendorDirectory = Path.Combine(AppDataPaths.Directory(sp.GetRequiredService<IAppInfoProvider>()), "yt-dlp"),
        });
        services.TryAddSingleton<IYtDlpLocator, YtDlpLocator>();
        services.TryAddSingleton(YtDlpManifest.Default);
        services.TryAddSingleton<IYtDlpProvisioner, YtDlpProvisioner>();
        services.TryAddSingleton<IYtDlpRunner, YtDlpRunner>();

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

        // DASH SegmentTemplate/SegmentList (TASK-102): downloads a representation's init + media segments
        // (re-resolved from the manifest at download time) with bounded parallelism, mirroring the HLS path.
        services.TryAddSingleton(new DashOptions());
        services.TryAddSingleton<IDashSegmentDownloader, DashSegmentDownloader>();

        // Hostile-site best-effort extractors (TASK-101, D3): YouTube and Facebook each recognise their own
        // host and degrade gracefully (return null) when nothing safely extractable is found. Registered
        // before the generic catch-alls via their low Priority.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, YouTubeMediaExtractor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, FacebookMediaExtractor>());

        // Optional yt-dlp fallback extractor (TASK-163, D3): the true last resort, registered with
        // Priority = int.MaxValue so it always runs after every extractor above, including Progressive's
        // catch-all (lowered to 1000 to make room). Declines instantly, spawning no subprocess, unless the
        // user has both enabled video capture in Settings and already provisioned yt-dlp (TASK-162).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, YtDlpMediaExtractor>());

        // A/V mux (TASK-041): stream-copy the two streams into one container (MKV default, MP4 when codecs allow).
        services.TryAddSingleton<IMediaMuxer, MediaMuxer>();

        services.TryAddSingleton<IMediaExtractorRegistry, MediaExtractorRegistry>();

        // Orchestrates a chosen media variant into a tracked download (TASK-154): HLS segments -> concat.
        services.TryAddSingleton<IMediaDownloadCoordinator, MediaDownloadCoordinator>();

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

        // The host validates the calling extension against this allowlist (US-11 AC4). Derived from the
        // single identity source so it never drifts from the registered manifests (TASK-090).
        services.TryAddSingleton(new NativeHostOptions
        {
            AllowedExtensionIds = NativeHostIdentity.AllowedExtensionIds,
        });

        // Hand-off queue + app launcher (TASK-070, US-11 AC5): the host queues links the app drains on next
        // start, and launches the app when it is not already running.
        services.TryAddSingleton<IExtensionInbox, ExtensionInbox>();
        services.TryAddSingleton<IAppRunningProbe, AppRunningProbe>();
        services.TryAddSingleton<IAppLauncher, AppLauncher>();

        // Real "has a browser's extension actually contacted us" tracking (TASK-175), independent of whether
        // the host manifest file merely exists (that gets written on every app startup regardless).
        services.TryAddSingleton<IExtensionContactTracker, ExtensionContactTracker>();

        // The extension router handles ping, blacklist sync (TASK-069), and download links — queueing them and
        // launching the app (TASK-070). Replaces the bare ping handler from TASK-064.
        services.TryAddSingleton<INativeMessageHandler, ExtensionMessageHandler>();
        services.TryAddSingleton<NativeMessageHost>();

        // Native-messaging host manifest registration (TASK-065, US-11): per-OS manifest locations, the
        // registry writer (Windows only; macOS/Linux register by file path), and the registrar that writes
        // and removes the Chrome/Edge/Firefox manifests on install/uninstall.
        services.TryAddSingleton<INativeHostManifestLocations, NativeHostManifestLocations>();
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<INativeHostRegistryWriter, WindowsNativeHostRegistryWriter>();
        }
        else
        {
            services.TryAddSingleton<INativeHostRegistryWriter, NoOpNativeHostRegistryWriter>();
        }

        services.TryAddSingleton<INativeHostRegistrar, NativeHostRegistrar>();

        // Self-registration on app startup so browsers can find/launch the host (TASK-089), and the
        // in-app Browsers panel's status/register/unregister (TASK-093).
        services.TryAddSingleton<INativeHostInstaller, NativeHostInstaller>();

        return services;
    }

    /// <summary>
    /// Registers the opt-in GitHub Releases update checker (TASK-080, PRD 6.3): version detection, ECDSA
    /// P-256 signature verification of a release's checksums manifest and SHA-256 verification of the
    /// target asset before trusting anything, then launching the verified installer via a per-OS
    /// <see cref="IUpdateApplier"/> (Windows/macOS/Linux, TASK-172 — see <c>UpdateChecker.ResolveInstallerAssetName</c>
    /// for how the per-OS/arch asset name is picked). Nothing is ever fetched unless the user opts in via
    /// <see cref="Settings.AppSettings.AutoUpdateEnabled"/>.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadUpdates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAppVersionProvider, AppVersionProvider>();
        services.TryAddSingleton(new UpdateOptions());
        services.TryAddSingleton<IUpdateChecker, UpdateChecker>();

        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IUpdateApplier, WindowsUpdateApplier>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<IUpdateApplier, MacOsUpdateApplier>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.TryAddSingleton<IUpdateApplier, LinuxUpdateApplier>();
        }
        else
        {
            services.TryAddSingleton<IUpdateApplier, UnsupportedUpdateApplier>();
        }

        return services;
    }
}
