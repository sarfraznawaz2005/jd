using System.Text.Json;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Tracks when the native host last actually heard from each extension origin (TASK-175), so the app can
/// show a real "is the extension installed and talking to us" signal instead of "did we write a host
/// manifest file" —
/// the manifest gets written automatically on every app startup (<see cref="INativeHostInstaller.Install"/>)
/// regardless of whether any extension exists. The host can only ever observe contact the browser itself
/// initiates (native messaging has no polling/probing), so this is a passive record: every
/// <c>JustDownload.NativeHost</c> launch with a valid, allow-listed origin (<see cref="ExtensionOrigin"/>)
/// records itself here before the message loop even starts, so registering contact never depends on the
/// extension sending a particular message type.
/// </summary>
public interface IExtensionContactTracker
{
    /// <summary>Records that <paramref name="origin"/> contacted the host just now.</summary>
    Task RecordContactAsync(ExtensionContactOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>The last time <paramref name="origin"/> contacted the host, or null if never observed.</summary>
    DateTimeOffset? GetLastContact(ExtensionContactOrigin origin);
}

/// <summary>Default <see cref="IExtensionContactTracker"/> backed by a JSON file under the app-data directory,
/// mirroring <see cref="ExtensionInbox"/>'s pattern for state shared between the host and app processes.</summary>
public sealed class ExtensionContactTracker : IExtensionContactTracker, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    /// <summary>Creates a tracker backed by an explicit file path (used by tests).</summary>
    public ExtensionContactTracker(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _path = filePath;
    }

    /// <summary>Creates a tracker under the engine data directory (the DI default; honors JUSTDOWNLOAD_DATA_DIR).</summary>
    public ExtensionContactTracker(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        _path = Path.Combine(AppDataPaths.Directory(appInfo), "extension-contacts.json");
    }

    public async Task RecordContactAsync(ExtensionContactOrigin origin, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExtensionContactRecord record = Read();
            record = origin == ExtensionContactOrigin.Chromium
                ? record with { ChromiumLastSeenUtc = DateTimeOffset.UtcNow }
                : record with { FirefoxLastSeenUtc = DateTimeOffset.UtcNow };

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using FileStream stream = File.Create(_path);
            await JsonSerializer
                .SerializeAsync(stream, record, NativeMessagingJsonContext.Default.ExtensionContactRecord, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public DateTimeOffset? GetLastContact(ExtensionContactOrigin origin)
    {
        ExtensionContactRecord record = Read();
        return origin == ExtensionContactOrigin.Chromium ? record.ChromiumLastSeenUtc : record.FirefoxLastSeenUtc;
    }

    private ExtensionContactRecord Read()
    {
        if (!File.Exists(_path))
        {
            return new ExtensionContactRecord();
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, NativeMessagingJsonContext.Default.ExtensionContactRecord)
                ?? new ExtensionContactRecord();
        }
        catch (JsonException)
        {
            return new ExtensionContactRecord(); // a corrupt record is discarded, not fatal
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>The on-disk shape of the contact record (TASK-175). A fixed-shape DTO so it serializes through
/// the source-generated context rather than a reflection-built object.</summary>
internal sealed record ExtensionContactRecord
{
    public DateTimeOffset? ChromiumLastSeenUtc { get; init; }

    public DateTimeOffset? FirefoxLastSeenUtc { get; init; }
}
