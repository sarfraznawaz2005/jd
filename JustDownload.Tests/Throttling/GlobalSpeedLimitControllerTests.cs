using FluentAssertions;
using JustDownload.Core.Settings;
using JustDownload.Core.Throttling;
using JustDownload.Tests.Fakes;
using Xunit;

namespace JustDownload.Tests.Throttling;

/// <summary>
/// Tests that the global speed limit setting actually drives the shared rate limiter (US-3): applied on
/// startup from the loaded value (TASK-088 AC0/AC2) and updated live when the user changes it (AC1).
/// </summary>
public sealed class GlobalSpeedLimitControllerTests
{
    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; set; } = new();

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AppSettings> UpdateAsync(
            Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            AppSettings previous = Current;
            Current = mutate(previous);
            Changed?.Invoke(this, new SettingsChangedEventArgs(previous, Current, ["downloads.global_speed_limit"]));
            return Task.FromResult(Current);
        }
    }

    [Fact]
    public void ApplyCurrent_AppliesLoadedLimit_ToRateLimiter()
    {
        var settings = new FakeSettings { Current = new AppSettings { GlobalSpeedLimitBytesPerSecond = 256 * 1024 } };
        var limiter = new TokenBucket(new TestClock());
        using var controller = new GlobalSpeedLimitController(settings, limiter);

        controller.ApplyCurrent();

        limiter.BytesPerSecond.Should().Be(256 * 1024);
    }

    [Fact]
    public void SettingsChange_UpdatesRateLimiter_Live()
    {
        var settings = new FakeSettings();
        var limiter = new TokenBucket(new TestClock());
        using var controller = new GlobalSpeedLimitController(settings, limiter);
        controller.ApplyCurrent();
        limiter.BytesPerSecond.Should().Be(0, "no limit was loaded");

        _ = settings.UpdateAsync(s => s with { GlobalSpeedLimitBytesPerSecond = 1_048_576 });

        limiter.BytesPerSecond.Should().Be(1_048_576, "a live change must reach the shared limiter");
    }

    [Fact]
    public void Dispose_StopsTrackingFurtherChanges()
    {
        var settings = new FakeSettings();
        var limiter = new TokenBucket(new TestClock());
        var controller = new GlobalSpeedLimitController(settings, limiter);

        controller.Dispose();
        _ = settings.UpdateAsync(s => s with { GlobalSpeedLimitBytesPerSecond = 999 });

        limiter.BytesPerSecond.Should().Be(0, "a disposed controller must unsubscribe");
    }
}
