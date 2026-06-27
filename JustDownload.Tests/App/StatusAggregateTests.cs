using FluentAssertions;
using JustDownload.App.ViewModels;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the pure status-bar aggregation (TASK-049 AC2).</summary>
public sealed class StatusAggregateTests
{
    [Fact]
    public void SumsActiveSpeedsAndConnections()
    {
        var agg = new StatusAggregate();
        agg.Activate(1);
        agg.Activate(2);
        agg.Update(1, 442_000, 8);
        agg.Update(2, 1_200_000, 16);

        agg.ActiveCount.Should().Be(2);
        agg.TotalBytesPerSecond.Should().Be(1_642_000);
        agg.TotalConnections.Should().Be(24);
    }

    [Fact]
    public void Deactivate_RemovesFromTotals()
    {
        var agg = new StatusAggregate();
        agg.Update(1, 100, 4);
        agg.Update(2, 200, 8);

        agg.Deactivate(1);

        agg.ActiveCount.Should().Be(1);
        agg.TotalBytesPerSecond.Should().Be(200);
        agg.TotalConnections.Should().Be(8);
    }

    [Fact]
    public void Activate_IsIdempotent_AndPreservesUpdatedFigures()
    {
        var agg = new StatusAggregate();
        agg.Activate(1);
        agg.Update(1, 500, 4);
        agg.Activate(1); // a duplicate activation must not reset its figures

        agg.ActiveCount.Should().Be(1);
        agg.TotalBytesPerSecond.Should().Be(500);
        agg.TotalConnections.Should().Be(4);
    }

    [Fact]
    public void Empty_HasZeroTotals()
    {
        var agg = new StatusAggregate();
        agg.ActiveCount.Should().Be(0);
        agg.TotalBytesPerSecond.Should().Be(0);
        agg.TotalConnections.Should().Be(0);
    }
}
