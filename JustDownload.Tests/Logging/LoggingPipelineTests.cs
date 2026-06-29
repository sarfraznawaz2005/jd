using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JustDownload.Tests.Logging;

/// <summary>
/// End-to-end checks on the logging pipeline wired by the Core composition root (TASK-016 /
/// TASK-017): secrets are redacted in real log output (criterion [1]) and the minimum level is
/// configurable (criterion [2]).
/// </summary>
public class LoggingPipelineTests
{
    private static (ServiceProvider Provider, CapturingLoggerProvider Sink) BuildPipeline(
        Action<LoggingOptions>? configure = null)
    {
        var sink = new CapturingLoggerProvider();
        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ILoggerProvider>(sink)
            .AddJustDownloadCore(configure)
            .BuildServiceProvider();

        return (provider, sink);
    }

    [Fact]
    public void Pipeline_RedactsSecrets_InActualLogOutput()
    {
        (ServiceProvider provider, CapturingLoggerProvider sink) = BuildPipeline();
        using (provider)
        {
            ILogger logger = provider.GetRequiredService<ILogger<LoggingPipelineTests>>();

            logger.LogInformation(
                "Requesting {Url} with header Authorization: Bearer topsecretvalue12345",
                "https://cdn.example.com/v.mp4?X-Amz-Signature=abc123def456ghijkl");

            sink.Entries.Should().ContainSingle();
            sink.Entries.TryDequeue(out (LogLevel Level, string Message) entry).Should().BeTrue();

            entry.Message.Should().Contain(SecretRedactor.Mask);
            entry.Message.Should().NotContain("topsecretvalue12345");
            entry.Message.Should().NotContain("abc123def456ghijkl");
        }
    }

    [Fact]
    public void Pipeline_UsesRedactingLoggerFactory()
    {
        (ServiceProvider provider, _) = BuildPipeline();
        using (provider)
        {
            ILoggerFactory factory = provider.GetRequiredService<ILoggerFactory>();
            factory.GetType().Name.Should().Be("RedactingLoggerFactory");
        }
    }

    [Fact]
    public void Pipeline_DefaultLevel_IsInformation()
    {
        (ServiceProvider provider, _) = BuildPipeline();
        using (provider)
        {
            ILogger logger = provider.GetRequiredService<ILogger<LoggingPipelineTests>>();

            logger.IsEnabled(LogLevel.Information).Should().BeTrue();
            logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
        }
    }

    [Fact]
    public void Pipeline_LevelSwitch_ChangesVerbosityLive_BothWays()
    {
        (ServiceProvider provider, CapturingLoggerProvider sink) = BuildPipeline(); // default Information
        using (provider)
        {
            ILogger logger = provider.GetRequiredService<ILogger<LoggingPipelineTests>>();
            var levelSwitch = provider.GetRequiredService<ILogLevelSwitch>();

            logger.IsEnabled(LogLevel.Debug).Should().BeFalse("Debug is below the default Information");

            // Lower the threshold at runtime — Debug now flows (proves the switch, not a fixed startup filter).
            levelSwitch.Minimum = LogLevel.Debug;
            logger.IsEnabled(LogLevel.Debug).Should().BeTrue();
            logger.LogDebug("now visible");

            // Raise it — Information is suppressed again.
            levelSwitch.Minimum = LogLevel.Warning;
            logger.IsEnabled(LogLevel.Information).Should().BeFalse();
            logger.LogInformation("now hidden");
            logger.LogWarning("kept");

            sink.Entries.Select(e => e.Message).Should().BeEquivalentTo(["now visible", "kept"]);
        }
    }

    [Fact]
    public void Pipeline_MinimumLevel_IsConfigurable()
    {
        (ServiceProvider provider, CapturingLoggerProvider sink) =
            BuildPipeline(o => o.MinimumLevel = LogLevel.Warning);
        using (provider)
        {
            ILogger logger = provider.GetRequiredService<ILogger<LoggingPipelineTests>>();

            logger.IsEnabled(LogLevel.Information).Should().BeFalse();
            logger.IsEnabled(LogLevel.Warning).Should().BeTrue();

            // Below-threshold messages are filtered out before reaching the sink.
            logger.LogInformation("this should be filtered");
            logger.LogWarning("this should pass");

            sink.Entries.Should().ContainSingle();
            sink.Entries.TryDequeue(out (LogLevel Level, string Message) entry).Should().BeTrue();
            entry.Level.Should().Be(LogLevel.Warning);
            entry.Message.Should().Be("this should pass");
        }
    }
}
