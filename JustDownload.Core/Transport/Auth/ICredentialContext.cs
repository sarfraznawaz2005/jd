namespace JustDownload.Core.Transport.Auth;

/// <summary>
/// Carries a download's origin credentials to the transport without threading them through the request API
/// (TASK-035), mirroring how the proxy override flows. A scope opened at the start of a download applies to
/// its probe and every segment worker; <see cref="Effective"/> is the credentials in effect for the current
/// async flow (or <see langword="null"/> for none).
/// </summary>
public interface ICredentialContext
{
    /// <summary>The origin credentials for the current async flow, or <see langword="null"/>.</summary>
    NetworkCredentials? Effective { get; }

    /// <summary>Begins a credential scope for the current flow; dispose to clear it. <see langword="null"/> installs no scope.</summary>
    IDisposable BeginScope(NetworkCredentials? credentials);
}
