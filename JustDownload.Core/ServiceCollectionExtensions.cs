using JustDownload.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// caller (notably a test) can pre-register a substitute and have it win.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddJustDownloadCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAppInfoProvider, AppInfoProvider>();

        return services;
    }
}
