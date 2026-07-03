using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Computes the OS pipe name for the single-instance coordination channel (TASK-182), shared by
/// JustDownload.App's <c>SingleInstanceCoordinator</c> (the listening owner instance) and
/// JustDownload.Core's <see cref="AppLauncher"/> (a client — the native host process signaling an
/// already-running app instance that a browser hand-off just arrived) so both sides derive the exact same
/// pipe name without duplicating the hashing logic. Hashed rather than used verbatim because named pipes
/// map to a Unix domain socket on macOS/Linux, which has a hard OS path-length limit.
/// </summary>
public static class SingleInstancePipeName
{
    /// <summary>The human-readable base name both sides agree on.</summary>
    public const string BaseName = "JustDownload.SingleInstance";

    /// <summary>
    /// Sent in place of a forwarded URL argument to mean "re-check the extension inbox now" (TASK-182) —
    /// never a valid absolute URI, so it can't collide with an actual forwarded link.
    /// </summary>
    public const string DrainInboxSignal = "__jd_drain_inbox__";

    /// <summary>Resolves <paramref name="baseName"/> to its pipe name.</summary>
    public static string Resolve(string baseName = BaseName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseName)))[..16];
}
