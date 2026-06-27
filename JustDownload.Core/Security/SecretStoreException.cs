namespace JustDownload.Core.Security;

/// <summary>
/// Raised when the OS secret vault rejects a store/retrieve/delete operation (for example the
/// keychain helper is missing or returns an error). Carries no secret material — only the operation
/// outcome — so it is safe to surface through the global error handler and logs (CLAUDE.md §5).
/// </summary>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException()
    {
    }

    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
