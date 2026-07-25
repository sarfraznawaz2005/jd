using JustDownload.Core.Abstractions;

namespace JustDownload.Tests.Fakes;

/// <summary>A controllable <see cref="IMonotonicClock"/> for deterministic elapsed-time tests.</summary>
internal sealed class TestMonotonicClock : IMonotonicClock
{
    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan delta) => Elapsed += delta;
}
