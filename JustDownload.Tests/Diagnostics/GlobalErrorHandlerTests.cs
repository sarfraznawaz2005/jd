using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Diagnostics;
using JustDownload.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JustDownload.Tests.Diagnostics;

/// <summary>
/// Verifies the global error handler (TASK-016, criterion [0]): unhandled errors are captured,
/// logged loudly (never swallowed), and surfaced to subscribers.
/// </summary>
public class GlobalErrorHandlerTests
{
    private static (IGlobalErrorHandler Handler, CapturingLoggerProvider Sink, ServiceProvider Provider) Build()
    {
        var sink = new CapturingLoggerProvider();
        ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ILoggerProvider>(sink)
            .AddJustDownloadCore()
            .BuildServiceProvider();

        return (provider.GetRequiredService<IGlobalErrorHandler>(), sink, provider);
    }

    [Fact]
    public void Handle_RaisesErrorOccurred_WithExceptionAndSource()
    {
        (IGlobalErrorHandler handler, _, ServiceProvider provider) = Build();
        using (provider)
        {
            var raised = new List<UnhandledErrorEventArgs>();
            handler.ErrorOccurred += (_, e) => raised.Add(e);

            var boom = new InvalidOperationException("boom");
            handler.Handle(boom, "unit-test");

            raised.Should().ContainSingle();
            raised[0].Exception.Should().BeSameAs(boom);
            raised[0].Source.Should().Be("unit-test");
            raised[0].IsTerminating.Should().BeFalse();
        }
    }

    [Fact]
    public void Handle_LogsCritical_NeverSilent()
    {
        (IGlobalErrorHandler handler, CapturingLoggerProvider sink, ServiceProvider provider) = Build();
        using (provider)
        {
            handler.Handle(new InvalidOperationException("boom"), "unit-test");

            sink.Entries.Should().Contain(e => e.Level == LogLevel.Critical);
        }
    }

    [Fact]
    public void Handle_RedactsSecrets_InTheSourceText()
    {
        (IGlobalErrorHandler handler, CapturingLoggerProvider sink, ServiceProvider provider) = Build();
        using (provider)
        {
            handler.Handle(new InvalidOperationException("failed"), "GET /f?token=supersecretvalue123");

            sink.Entries.Should().Contain(e =>
                e.Level == LogLevel.Critical && !e.Message.Contains("supersecretvalue123"));
        }
    }

    [Fact]
    public void Handle_DoesNotPropagate_WhenASubscriberThrows()
    {
        (IGlobalErrorHandler handler, CapturingLoggerProvider sink, ServiceProvider provider) = Build();
        using (provider)
        {
            handler.ErrorOccurred += (_, _) => throw new InvalidOperationException("subscriber failed");

            Action act = () => handler.Handle(new InvalidOperationException("boom"), "unit-test");

            act.Should().NotThrow("a faulty subscriber must not mask the original error");
            // Both the original (Critical) and the subscriber failure (Error) are logged.
            sink.Entries.Should().Contain(e => e.Level == LogLevel.Critical);
            sink.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        }
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        (IGlobalErrorHandler handler, _, ServiceProvider provider) = Build();
        using (provider)
        {
            Action act = () =>
            {
                handler.Install();
                handler.Install();
            };

            act.Should().NotThrow();
        }
    }
}
