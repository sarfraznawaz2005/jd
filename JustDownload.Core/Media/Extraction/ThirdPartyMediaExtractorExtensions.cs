using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// Public registration seam for third-party <see cref="IMediaExtractor"/> implementations (TASK-150).
/// A consumer that references <c>JustDownload.Core</c> (their own project, or a fork's own composition
/// code) calls this on their <see cref="IServiceCollection"/> alongside <c>AddJustDownloadMedia()</c> to
/// add a site-specific extractor to the <see cref="IMediaExtractorRegistry"/> chain, without editing
/// <c>ServiceCollectionExtensions.cs</c> or any other file inside <c>JustDownload.Core</c>. Call order
/// relative to <c>AddJustDownloadMedia()</c> does not matter — the registry sorts by
/// <see cref="IMediaExtractor.Priority"/> once at construction, not by registration order.
/// </summary>
public static class ThirdPartyMediaExtractorExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TExtractor"/> as an <see cref="IMediaExtractor"/> the registry will
    /// try alongside the built-in extractors. Uses <see cref="ServiceCollectionServiceExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// — the same mechanism the built-in extractors use — so registering the same type twice is a no-op.
    /// Pick a <see cref="IMediaExtractor.Priority"/> in the open 100–999 band to run after the protocol-level
    /// HLS/DASH extractors but before the generic progressive catch-all, or wherever else fits your extractor.
    /// </summary>
    /// <typeparam name="TExtractor">The third-party extractor implementation to register.</typeparam>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddThirdPartyMediaExtractor<TExtractor>(this IServiceCollection services)
        where TExtractor : class, IMediaExtractor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaExtractor, TExtractor>());

        return services;
    }
}
