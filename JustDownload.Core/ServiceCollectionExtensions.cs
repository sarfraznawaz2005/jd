using JustDownload.Core.Abstractions;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.Logging;
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

        return services;
    }
}
