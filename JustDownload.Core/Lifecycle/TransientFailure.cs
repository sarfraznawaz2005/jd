using System.Net.Http;
using System.Net.Sockets;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Classifies a download failure as transient — a network glitch that may well succeed on a retry (TASK-131)
/// — versus a permanent one that retrying cannot fix. Auth-required, expired-link, and resume-not-supported
/// failures have their own handling and are deliberately <b>not</b> transient (retrying them just fails
/// again); a user pause (an <see cref="OperationCanceledException"/> on the caller's token) is handled before
/// this is ever consulted.
/// </summary>
internal static class TransientFailure
{
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Unwrap: an HttpRequestException or IOException often wraps the real SocketException, and a request
        // timeout surfaces as a TaskCanceledException not tied to the caller's cancellation token.
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException or IOException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}
