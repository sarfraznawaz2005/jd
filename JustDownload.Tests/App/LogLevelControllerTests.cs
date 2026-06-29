using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Logging;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The log-level controller (TASK-127): reconciles the live <see cref="ILogLevelSwitch"/> with the persisted
/// minimum level on startup and on every settings change.
/// </summary>
public sealed class LogLevelControllerTests
{
    private sealed class Switch : ILogLevelSwitch
    {
        public LogLevel Minimum { get; set; } = LogLevel.Information;
    }

    private static ISettingsService Settings(LogLevel level)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { MinimumLogLevel = level });
        return settings;
    }

    [Fact]
    public void ApplyCurrent_SetsSwitchFromSettings()
    {
        var levelSwitch = new Switch();
        using var controller = new LogLevelController(levelSwitch, Settings(LogLevel.Warning));

        controller.ApplyCurrent();

        levelSwitch.Minimum.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void SettingsChange_UpdatesSwitchLive()
    {
        var levelSwitch = new Switch();
        var settings = Settings(LogLevel.Information);
        using var controller = new LogLevelController(levelSwitch, settings);

        settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settings,
            new SettingsChangedEventArgs(
                new AppSettings(), new AppSettings { MinimumLogLevel = LogLevel.Debug }, Array.Empty<string>()));

        levelSwitch.Minimum.Should().Be(LogLevel.Debug);
    }
}
