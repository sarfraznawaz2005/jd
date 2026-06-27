using JustDownload.Core.Abstractions;

namespace JustDownload.Tests.Fakes;

/// <summary>A controllable <see cref="IClock"/> for deterministic time-based tests.</summary>
internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
