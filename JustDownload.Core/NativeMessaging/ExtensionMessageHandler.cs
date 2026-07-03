using System.Text.Json;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Logging;
using JustDownload.Core.Settings;
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
    private readonly IExtensionInbox _inbox;
    private readonly IAppLauncher _launcher;
    private readonly IAppRunningProbe _appRunning;
    private readonly ISettingsService _settings;
    private readonly ILogger<ExtensionMessageHandler> _logger;

    public ExtensionMessageHandler(
        IBlacklistRepository blacklist,
        IExtensionInbox inbox,
        IAppLauncher launcher,
        IAppRunningProbe appRunning,
        ISettingsService settings,
        ILogger<ExtensionMessageHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(blacklist);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(appRunning);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _blacklist = blacklist;
        _inbox = inbox;
        _launcher = launcher;
        _appRunning = appRunning;
        _settings = settings;
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
                    // "pong" must mean the desktop app itself is running, not just that this native host
                    // process (a separate, short-lived process the browser can spawn on its own) answered
                    // (TASK-185) — before this, the extension popup showed "App connected" even with the
                    // app fully closed.
                    return _appRunning.IsRunning()
                        ? "{\"type\":\"pong\"}"
                        : "{\"type\":\"error\",\"error\":\"app_not_running\"}";

                case "blacklist_sync":
                    await SyncBlacklistAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
                    return "{\"type\":\"ok\"}";

                case "download_link":
                    await AcceptDownloadAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
                    return "{\"type\":\"ok\"}";

                case "get_settings":
                    return await GetSettingsAsync(cancellationToken).ConfigureAwait(false);

                case null:
                    return "{\"type\":\"error\",\"error\":\"malformed message\"}";

                default:
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

    private async Task AcceptDownloadAsync(JsonElement root, CancellationToken cancellationToken)
    {
        string? url = ReadString(root, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        // Queue the link durably so it's delivered whether the app is running now or starts later (AC1),
        // then ensure the app is running so it acts on it promptly (AC0).
        await _inbox.EnqueueAsync(
            new PendingLink
            {
                Url = url,
                Referrer = ReadString(root, "referrer"),
                Cookies = ReadString(root, "cookies"),
                MediaKind = ReadString(root, "mediaKind"),
            },
            cancellationToken).ConfigureAwait(false);

        await _launcher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1873 // SafeLogUrl.Of is a cheap Uri.TryCreate + string interpolation, not worth an IsEnabled guard
        LogQueued(_logger, SafeLogUrl.Of(url));
#pragma warning restore CA1873
    }

    private async Task<string> GetSettingsAsync(CancellationToken cancellationToken)
    {
        // Hydrate from storage so the popup reflects the user's actual app settings (TASK-071 AC2).
        await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        AppSettings s = _settings.Current;
        var payload = new ExtensionSettingsDto
        {
            DefaultVideoQuality = (int)s.DefaultVideoQuality,
            DefaultContainer = s.DefaultContainer.ToString().ToLowerInvariant(),
            MaxConcurrentDownloads = s.MaxConcurrentDownloads,
            VideoCaptureEnabled = s.VideoCaptureEnabled,
        };
        return JsonSerializer.Serialize(payload, NativeMessagingJsonContext.Default.ExtensionSettingsDto);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadType(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Synced {Count} blacklisted site(s) from the extension.")]
    private static partial void LogSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Queued a handed-off link from {Source} for the desktop app.")]
    private static partial void LogQueued(ILogger logger, string source);
}
