using JustDownload.Core.Lifecycle;

namespace JustDownload.Tests.Fakes;

/// <summary>
/// A deterministic <see cref="IRetryBackoff"/> for tests: a fixed retry budget and <b>zero</b> delay, so
/// auto-retry behaviour (TASK-131) can be exercised without real wall-clock waits.
/// </summary>
internal sealed class FakeRetryBackoff : IRetryBackoff
{
    public FakeRetryBackoff(int maxRetries) => MaxRetries = maxRetries;

    public int MaxRetries { get; }

    public TimeSpan DelayFor(int retryNumber) => TimeSpan.Zero;
}
