using System.Net.Http;

namespace JustDownload.Core.Transport;

/// <summary>
/// Owns the single, process-wide <see cref="SocketsHttpHandler"/> that every HTTP request flows through
/// (TASK-023 AC2, CLAUDE.md §5 "a single shared SocketsHttpHandler"). One handler means one connection
/// pool, which is what lets the segmentation engine open many connections to a host efficiently without
/// socket churn or exhaustion. Registered as a singleton and disposed with the container.
/// </summary>
public interface ISharedHttpHandlerProvider : IDisposable
{
    /// <summary>The shared handler. The same instance is returned for the provider's lifetime.</summary>
    SocketsHttpHandler Handler { get; }
}
