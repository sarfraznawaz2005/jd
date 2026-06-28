namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>The browsers JustDownload registers its native-messaging host with (TASK-065, US-11).</summary>
public enum NativeMessagingBrowser
{
    /// <summary>Google Chrome (Chromium <c>allowed_origins</c> manifest).</summary>
    Chrome,

    /// <summary>Microsoft Edge (Chromium <c>allowed_origins</c> manifest).</summary>
    Edge,

    /// <summary>Mozilla Firefox (<c>allowed_extensions</c> manifest).</summary>
    Firefox,
}
