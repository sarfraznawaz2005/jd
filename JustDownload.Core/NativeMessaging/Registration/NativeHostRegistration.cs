namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// The data a native-messaging host manifest is built from (TASK-065): the host <see cref="Name"/> (which
/// the extension's <c>nativeMessaging.connectNative</c> uses), a description, the absolute
/// <see cref="ExecutablePath"/> the browser launches, and the callers permitted to connect — Chromium
/// <see cref="AllowedOrigins"/> (<c>chrome-extension://&lt;id&gt;/</c>) and Firefox
/// <see cref="AllowedExtensionIds"/> (the gecko id).
/// </summary>
public sealed record NativeHostRegistration
{
    /// <summary>The native host name (must match the extension and the host manifest). Reverse-DNS by convention.</summary>
    public required string Name { get; init; }

    /// <summary>A short human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>The absolute path to the native host executable the browser launches.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The Chromium extension origins permitted to connect (e.g. <c>chrome-extension://abc.../</c>).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>The Firefox extension ids permitted to connect (the gecko id).</summary>
    public IReadOnlyList<string> AllowedExtensionIds { get; init; } = [];
}
