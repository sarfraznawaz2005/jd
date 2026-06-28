using System.Text.Json;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// A durable hand-off queue between the native host and the desktop app (TASK-070, US-11 AC5). When the
/// extension sends a link while the app is not running, the host appends it here; the app drains it on the
/// next start. Persisted as a small JSON file in the app-data directory so it survives across processes and
/// restarts, and shared by both the host and app processes.
/// </summary>
public interface IExtensionInbox
{
    /// <summary>Appends a pending link to the inbox.</summary>
    Task EnqueueAsync(PendingLink link, CancellationToken cancellationToken = default);

    /// <summary>Returns all pending links and clears the inbox (the app calls this once on start).</summary>
    Task<IReadOnlyList<PendingLink>> DrainAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IExtensionInbox"/> backed by a JSON file under the app-data directory (TASK-070).</summary>
public sealed class ExtensionInbox : IExtensionInbox, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = false };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    /// <summary>Creates an inbox backed by an explicit file path (used by tests).</summary>
    public ExtensionInbox(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _path = filePath;
    }

    /// <summary>Creates an inbox under <c>%APPDATA%/&lt;app&gt;/extension-inbox.json</c> (the DI default).</summary>
    public ExtensionInbox(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        _path = Path.Combine(appData, appInfo.Name, "extension-inbox.json");
    }

    public async Task EnqueueAsync(PendingLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PendingLink> links = await ReadAsync(cancellationToken).ConfigureAwait(false);
            links.Add(link);
            await WriteAsync(links, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingLink>> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PendingLink> links = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (links.Count > 0 && File.Exists(_path))
            {
                File.Delete(_path);
            }

            return links;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PendingLink>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            List<PendingLink>? links = await JsonSerializer
                .DeserializeAsync<List<PendingLink>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return links ?? [];
        }
        catch (JsonException)
        {
            return []; // a corrupt inbox is discarded rather than blocking startup
        }
    }

    private async Task WriteAsync(List<PendingLink> links, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using FileStream stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, links, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();
}
