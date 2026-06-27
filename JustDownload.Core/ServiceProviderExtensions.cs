using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace JustDownload.Core;

/// <summary>
/// Async startup initialisation for the headless engine. The composition root (<see cref="ServiceCollectionExtensions"/>)
/// only wires services; anything that must run asynchronously once at startup lives here so every host —
/// <c>JustDownload.App</c>, <c>JustDownload.NativeHost</c>, the test suite, a future CLI — initialises Core
/// the same way.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Runs Core's one-time async startup initialisation. It first brings the database schema up to date
    /// (so a freshly-installed host has its tables before anything reads them), then applies the user's
    /// persisted categorisation overrides over the seeded defaults (TASK-085). Later startup work (e.g.
    /// the crash-recovery resume scan) can hang off the same seam. Hosts call this once, after building
    /// the service provider; it is idempotent.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="cancellationToken">Cancels the initialisation.</param>
    public static async Task InitializeJustDownloadCoreAsync(
        this IServiceProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Ensure the schema exists/upgrades before any repository reads it (idempotent).
        await provider.GetRequiredService<IMigrationRunner>()
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        await provider.GetRequiredService<ICategoryRuleService>()
            .ApplyPersistedOverridesAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
