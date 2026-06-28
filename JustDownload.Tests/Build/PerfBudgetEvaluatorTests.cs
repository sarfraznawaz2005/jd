using FluentAssertions;
using JustDownload.Perf;
using Xunit;

namespace JustDownload.Tests.Build;

/// <summary>
/// Unit tests for the performance-budget gate logic (TASK-015). The bundle KPI is hard-gated (a regression
/// fails CI); cold-start and idle-RAM are measured and reported but never fail the gate, matching the
/// project decision that GUI startup/RAM can't be measured reliably on a headless runner.
/// </summary>
[Trait("Category", "PerfBudget")]
public sealed class PerfBudgetEvaluatorTests
{
    private static PerfBudget Budget(params string[] gated) => new(1500, 90, 40, gated);

    [Fact]
    public void Bundle_OverBudget_IsAGatedFailure()
    {
        PerfReport report = PerfBudgetEvaluator.Evaluate(Budget("bundle"), new PerfMeasurement(100, 50, 41));

        report.HasGatedFailure.Should().BeTrue("the bundle exceeded its 40 MB budget and bundle is gated");
        report.Metrics.Single(m => m.Name == "bundle").IsGatedFailure.Should().BeTrue();
    }

    [Fact]
    public void Bundle_WithinBudget_Passes()
    {
        PerfReport report = PerfBudgetEvaluator.Evaluate(Budget("bundle"), new PerfMeasurement(100, 50, 31));

        report.HasGatedFailure.Should().BeFalse();
        report.Metrics.Single(m => m.Name == "bundle").WithinBudget.Should().BeTrue();
    }

    [Fact]
    public void StartupAndRam_OverBudget_AreTracked_NotGated()
    {
        // Cold-start and RAM both blow past budget, but neither is in the gated set.
        PerfReport report = PerfBudgetEvaluator.Evaluate(Budget("bundle"), new PerfMeasurement(9000, 900, 31));

        report.HasGatedFailure.Should().BeFalse("startup and RAM are tracked, not gated");
        report.Metrics.Single(m => m.Name == "coldStart").WithinBudget.Should().BeFalse();
        report.Metrics.Single(m => m.Name == "coldStart").IsGatedFailure.Should().BeFalse();
        report.Metrics.Single(m => m.Name == "idleRam").IsGatedFailure.Should().BeFalse();
    }

    [Fact]
    public void UnmeasuredBundle_IsTreatedAsWithinBudget()
    {
        PerfReport report = PerfBudgetEvaluator.Evaluate(Budget("bundle"), new PerfMeasurement(100, 50, null));

        report.HasGatedFailure.Should().BeFalse("a skipped bundle measurement can't regress the gate");
        report.Metrics.Single(m => m.Name == "bundle").WithinBudget.Should().BeTrue();
    }

    [Fact]
    public void GatingIsCaseInsensitive()
    {
        PerfReport report = PerfBudgetEvaluator.Evaluate(Budget("Bundle"), new PerfMeasurement(100, 50, 41));

        report.Metrics.Single(m => m.Name == "bundle").Gated.Should().BeTrue();
        report.HasGatedFailure.Should().BeTrue();
    }
}
