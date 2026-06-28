using FluentAssertions;
using JustDownload.Core.Downloading;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// The live connection-count controller (TASK-027): clamping, change notifications, and the engine-side
/// active-count write-back (AC2).
/// </summary>
public sealed class ConnectionControllerTests
{
    [Fact]
    public void Constructor_ClampsToAtLeastOne()
    {
        new ConnectionController(0).DesiredConnections.Should().Be(1);
        new ConnectionController(-5).DesiredConnections.Should().Be(1);
        new ConnectionController(4).DesiredConnections.Should().Be(4);
    }

    [Fact]
    public void SetDesiredConnections_UpdatesAndClamps_AndRaisesChanged()
    {
        var controller = new ConnectionController(4);
        var events = new List<ConnectionCountChangedEventArgs>();
        controller.Changed += (_, e) => events.Add(e);

        controller.SetDesiredConnections(8);
        controller.SetDesiredConnections(0); // clamps to 1

        controller.DesiredConnections.Should().Be(1);
        events.Should().HaveCount(2);
        events[0].DesiredConnections.Should().Be(8);
        events[1].DesiredConnections.Should().Be(1);
    }

    [Fact]
    public void SetDesiredConnections_NoChange_DoesNotRaise()
    {
        var controller = new ConnectionController(4);
        int raised = 0;
        controller.Changed += (_, _) => raised++;

        controller.SetDesiredConnections(4);

        raised.Should().Be(0);
    }

    [Fact]
    public void ReportActiveConnections_UpdatesActive_AndRaisesChanged()
    {
        var controller = new ConnectionController(4);
        var observed = new List<int>();
        controller.Changed += (_, e) => observed.Add(e.ActiveConnections);

        controller.ReportActiveConnections(3);
        controller.ReportActiveConnections(3); // no change → no event
        controller.ReportActiveConnections(0);

        controller.ActiveConnections.Should().Be(0);
        observed.Should().Equal(3, 0);
    }
}
