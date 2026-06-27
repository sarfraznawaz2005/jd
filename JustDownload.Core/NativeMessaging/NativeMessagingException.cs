namespace JustDownload.Core.NativeMessaging;

/// <summary>Raised on a native-messaging protocol violation (TASK-064): a truncated frame or an
/// oversized message beyond the configured limit.</summary>
public sealed class NativeMessagingException : Exception
{
    public NativeMessagingException()
    {
    }

    public NativeMessagingException(string message)
        : base(message)
    {
    }

    public NativeMessagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
