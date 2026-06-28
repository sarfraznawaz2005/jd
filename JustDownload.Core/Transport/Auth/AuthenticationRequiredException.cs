namespace JustDownload.Core.Transport.Auth;

/// <summary>
/// Thrown when a server (<c>401</c>) or proxy (<c>407</c>) demands authentication that was not satisfied —
/// either none was supplied or the supplied credentials were rejected (TASK-035 AC2). The lifecycle/UI
/// catches it to (re-)prompt for credentials and retry, rather than reporting a generic failure.
/// </summary>
public sealed class AuthenticationRequiredException : Exception
{
    /// <summary>Creates the exception for the given status (401 origin, 407 proxy).</summary>
    public AuthenticationRequiredException(int statusCode, bool isProxy)
        : base(isProxy
            ? "The proxy requires authentication (407)."
            : "The server requires authentication (401).")
    {
        StatusCode = statusCode;
        IsProxy = isProxy;
    }

    /// <summary>The HTTP status code that triggered this (<c>401</c> or <c>407</c>).</summary>
    public int StatusCode { get; }

    /// <summary>Whether the challenge came from the proxy (<c>407</c>) rather than the origin (<c>401</c>).</summary>
    public bool IsProxy { get; }
}
