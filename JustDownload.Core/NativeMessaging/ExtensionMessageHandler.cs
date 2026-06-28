using System.Text.Json;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Routes decoded native messages from the browser extension to the engine (TASK-067/069). It dispatches on
/// the message <c>type</c> (case-insensitive): a <c>ping</c> health check, and a <c>blacklist_sync</c> that
/// reconciles the per-site blacklist into the app's <c>site_blacklist</c> table (US-12) so the popup toggle
/// stays in sync with app settings. Download delivery (<c>download_link</c>) is handled by the
/// launch/queue path (TASK-070). Uses <see cref="JsonDocument"/> only — no reflection-based (de)serialization.
/// </summary>
internal sealed partial class ExtensionMessageHandler : INativeMessageHandler
{
    /// <summary>The blacklist scope the extension's floating-button blacklist is stored under.</summary>
    public const string ButtonScope = "button";

    private readonly IBlacklistRepository _blacklist;
    private readonly ILogger<ExtensionMessageHandler> _logger;

    public ExtensionMessageHandler(IBlacklistRepository blacklist, ILogger<ExtensionMessageHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(blacklist);
        ArgumentNullException.ThrowIfNull(logger);
        _blacklist = blacklist;
        _logger = logger;
    }

    public async Task<string?> HandleAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return "{\"type\":\"error\",\"error\":\"malformed message\"}";
        }

        using (document)
        {
            string? type = ReadType(document.RootElement)?.ToLowerInvariant();
            switch (type)
            {
                case "ping":
                    return "{\"type\":\"pong\"}";

                case "blacklist_sync":
                    await SyncBlacklistAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
                    return "{\"type\":\"ok\"}";

                case null:
                    return "{\"type\":\"error\",\"error\":\"malformed message\"}";

                default:
                    // Acknowledge other known types (e.g. download_link) so the extension's send succeeds;
                    // their delivery/enqueue is handled elsewhere (TASK-070).
                    return "{\"type\":\"ok\"}";
            }
        }
    }

    private async Task SyncBlacklistAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("domains", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } domain)
                {
                    domains.Add(domain);
                }
            }
        }

        // Reconcile: add the incoming domains, remove button-scoped entries no longer present.
        IReadOnlyList<BlacklistEntry> existing = await _blacklist.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (BlacklistEntry entry in existing)
        {
            if (entry.Scope == ButtonScope && !domains.Contains(entry.Domain))
            {
                await _blacklist.DeleteAsync(entry.Domain, ButtonScope, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (string domain in domains)
        {
            await _blacklist.AddAsync(
                new BlacklistEntry { Domain = domain, Scope = ButtonScope }, cancellationToken).ConfigureAwait(false);
        }

        LogSynced(_logger, domains.Count);
    }

    private static string? ReadType(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Synced {Count} blacklisted site(s) from the extension.")]
    private static partial void LogSynced(ILogger logger, int count);
}
