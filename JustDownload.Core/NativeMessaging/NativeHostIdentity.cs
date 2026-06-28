namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// The single source of truth for the native-messaging host identity (TASK-064/065/090): the host name the
/// extension connects to, and the extension ids permitted to talk to it. Both the host's runtime allowlist
/// (<see cref="NativeHostOptions.AllowedExtensionIds"/>) and the registered manifests
/// (<c>allowed_extensions</c> / <c>allowed_origins</c>) are derived from here so they can never drift apart.
/// <para>
/// The Firefox id is fixed by the extension's <c>browser_specific_settings.gecko.id</c>. The Chromium id is
/// non-deterministic until the extension ships a fixed manifest <c>key</c> (TASK-098) or is published; until
/// then <see cref="ChromiumExtensionId"/> is <see langword="null"/> and the Chromium path is not enabled
/// (the host rejects it and the Chromium manifest carries no origins) rather than trusting a guessed id.
/// </para>
/// </summary>
public static class NativeHostIdentity
{
    /// <summary>The native host name — must match the extension's <c>connectNative</c>/<c>sendNativeMessage</c>.</summary>
    public const string HostName = "app.justdownload.host";

    /// <summary>The Firefox extension id (its <c>gecko.id</c>).</summary>
    public const string FirefoxExtensionId = "justdownload@justdownload.app";

    /// <summary>
    /// The Chromium extension id (the bare 32-char id), once the extension has a fixed manifest <c>key</c>
    /// or is published (TASK-098). <see langword="null"/> means the Chromium path is intentionally not yet
    /// enabled — we never allowlist a guessed id. (A field, not a <c>const</c>, so the enabling logic below
    /// stays reachable until the id is known.)
    /// </summary>
    public static readonly string? ChromiumExtensionId;

    /// <summary>The Firefox ids written into the Firefox manifest's <c>allowed_extensions</c>.</summary>
    public static IReadOnlyList<string> FirefoxExtensionIds { get; } = [FirefoxExtensionId];

    /// <summary>The Chromium <c>chrome-extension://&lt;id&gt;/</c> origins for the Chromium manifest's <c>allowed_origins</c>.</summary>
    public static IReadOnlyList<string> ChromiumOrigins { get; } =
        ChromiumExtensionId is { Length: > 0 } id ? [$"chrome-extension://{id}/"] : [];

    /// <summary>
    /// Every id the host accepts as a launch argument (the Firefox id, and the Chromium id when enabled).
    /// <see cref="ExtensionOrigin.IsAllowed"/> substring-matches a launch argument against these, so the bare
    /// Chromium id matches its <c>chrome-extension://id/</c> origin.
    /// </summary>
    public static IReadOnlyList<string> AllowedExtensionIds { get; } = BuildAllowedIds();

    private static List<string> BuildAllowedIds()
    {
        var ids = new List<string> { FirefoxExtensionId };
        if (ChromiumExtensionId is { Length: > 0 } chromium)
        {
            ids.Add(chromium);
        }

        return ids;
    }
}
