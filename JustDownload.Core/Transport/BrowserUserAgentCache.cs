using System.Text.Json;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Transport;

/// <summary>
/// Persists the last browser-detected User-Agent so <see cref="BrowserUserAgentRefresher"/> only re-probes
/// installed browsers once the cached value goes stale, instead of spawning a process on every app start.
/// </summary>
internal interface IBrowserUserAgentCache
{
    /// <summary>Returns the cached User-Agent if it was resolved within <paramref name="maxAge"/>, else null.</summary>
    Task<string?> TryReadFreshAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="userAgent"/> as resolved just now.</summary>
    Task WriteAsync(string userAgent, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IBrowserUserAgentCache"/> backed by a JSON file under the app-data directory,
/// mirroring <see cref="NativeMessaging.ExtensionContactTracker"/>'s pattern for small persisted state.</summary>
internal sealed class BrowserUserAgentCache : IBrowserUserAgentCache, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    /// <summary>Creates a cache backed by an explicit file path (used by tests).</summary>
    public BrowserUserAgentCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _path = filePath;
    }

    /// <summary>Creates a cache under the engine data directory (the DI default; honors JUSTDOWNLOAD_DATA_DIR).</summary>
    public BrowserUserAgentCache(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        _path = Path.Combine(AppDataPaths.Directory(appInfo), "user-agent-cache.json");
    }

    public async Task<string?> TryReadFreshAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrowserUserAgentCacheEntry? entry = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (entry is null || DateTimeOffset.UtcNow - entry.ResolvedAtUtc > maxAge)
            {
                return null;
            }

            return entry.UserAgent;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(string userAgent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userAgent);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = new BrowserUserAgentCacheEntry(userAgent, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using FileStream stream = File.Create(_path);
            await JsonSerializer
                .SerializeAsync(stream, entry, BrowserUserAgentJsonContext.Default.BrowserUserAgentCacheEntry, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BrowserUserAgentCacheEntry?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return await JsonSerializer
                .DeserializeAsync(stream, BrowserUserAgentJsonContext.Default.BrowserUserAgentCacheEntry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null; // a corrupt cache entry just forces a fresh detection
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>The on-disk shape of the cached User-Agent.</summary>
internal sealed record BrowserUserAgentCacheEntry(string UserAgent, DateTimeOffset ResolvedAtUtc);
