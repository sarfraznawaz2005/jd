namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Configuration for the Native Messaging Host (TASK-064). Only browser extensions whose id is in
/// <see cref="AllowedExtensionIds"/> may talk to the host (US-11 AC4); the browser passes the calling
/// extension's origin as a launch argument, which the host validates before reading any message.
/// </summary>
public sealed class NativeHostOptions
{
    /// <summary>The extension ids permitted to connect (Chromium ids and/or the Firefox gecko id).</summary>
    public IReadOnlyList<string> AllowedExtensionIds { get; set; } = [];

    /// <summary>The maximum accepted incoming message size in bytes (default 1 MiB).</summary>
    public int MaxIncomingBytes { get; set; } = NativeMessageCodec.DefaultMaxMessageBytes;
}
