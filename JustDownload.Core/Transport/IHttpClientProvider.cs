using System.Net.Http;
using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Transport;

/// <summary>
/// Supplies the <see cref="HttpClient"/> for a given connection profile (TASK-034/035). The direct,
/// unauthenticated profile uses the single shared handler (TASK-023); each distinct proxy/credential
/// profile gets its own pooled handler, created once and reused, so there are only as many handlers as
/// there are profiles in play — keeping the engine light. Clients are cached by profile value.
/// </summary>
public interface IHttpClientProvider
{
    /// <summary>Returns the cached client for <paramref name="profile"/> (direct for <see cref="ConnectionProfile.Direct"/>).</summary>
    HttpClient GetClient(ConnectionProfile profile);
}
