using System.Diagnostics;

namespace JustDownload.Core.Abstractions;

/// <summary>Default <see cref="IMonotonicClock"/> backed by <see cref="Stopwatch"/>'s monotonic timestamp.</summary>
internal sealed class MonotonicClock : IMonotonicClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_origin);
}
