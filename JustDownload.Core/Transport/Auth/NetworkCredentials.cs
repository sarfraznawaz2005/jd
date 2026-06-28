namespace JustDownload.Core.Transport.Auth;

/// <summary>
/// A username/password (and optional NTLM <see cref="Domain"/>) used to answer an HTTP or proxy
/// authentication challenge (TASK-035, US-7). The same value answers Basic, Digest, and NTLM/Negotiate —
/// the scheme is chosen by the server's challenge, which .NET's handler negotiates. The plaintext password
/// lives only in memory while a download runs and at rest only in the OS keychain (AC1, see
/// <c>ICredentialStore</c>); it is never written to SQLite or logs.
/// </summary>
/// <param name="Username">The account user name.</param>
/// <param name="Password">The account password.</param>
/// <param name="Domain">The NTLM/Negotiate domain, or <see langword="null"/> for Basic/Digest.</param>
public sealed record NetworkCredentials(string Username, string Password, string? Domain = null);
