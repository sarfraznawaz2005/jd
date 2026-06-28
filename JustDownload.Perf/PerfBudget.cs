namespace JustDownload.Perf;

/// <summary>
/// The performance budget (TASK-015, PRD KPIs): the KPI limits and which of them are hard-gated in CI.
/// Cold-start and idle-RAM are recorded/tracked rather than gated, because a GUI app's startup and memory
/// can't be measured reliably on a headless CI runner; the bundle size is the deterministic, gated KPI.
/// </summary>
/// <param name="ColdStartMs">Cold-start budget in milliseconds (KPI ≤ 1500).</param>
/// <param name="IdleRamMb">Idle RAM budget in MB (KPI ≤ 90).</param>
/// <param name="BundleMb">Published bundle budget in MB (KPI ≤ 40).</param>
/// <param name="Gated">The metric names that fail CI on regression (e.g. <c>["bundle"]</c>).</param>
public sealed record PerfBudget(int ColdStartMs, int IdleRamMb, int BundleMb, IReadOnlyList<string> Gated);

/// <summary>A set of measured performance numbers (TASK-015).</summary>
/// <param name="ColdStartMs">Measured engine cold-start in milliseconds.</param>
/// <param name="IdleRamMb">Measured idle working-set RAM in MB.</param>
/// <param name="BundleMb">Measured published bundle size in MB (or <see langword="null"/> if not measured).</param>
public sealed record PerfMeasurement(double ColdStartMs, double IdleRamMb, double? BundleMb);

/// <summary>One metric's evaluation against its budget.</summary>
/// <param name="Name">The metric name (<c>coldStart</c> / <c>idleRam</c> / <c>bundle</c>).</param>
/// <param name="Measured">The measured value (null if it wasn't measured).</param>
/// <param name="Limit">The KPI limit.</param>
/// <param name="Gated">Whether exceeding this metric fails CI.</param>
public sealed record MetricResult(string Name, double? Measured, double Limit, bool Gated)
{
    /// <summary>Whether the measured value is within budget (true when not measured).</summary>
    public bool WithinBudget => Measured is not { } m || m <= Limit;

    /// <summary>Whether this is a gated metric that is over budget (i.e. a CI failure).</summary>
    public bool IsGatedFailure => Gated && !WithinBudget;
}

/// <summary>The full evaluation report.</summary>
/// <param name="Metrics">Per-metric results.</param>
public sealed record PerfReport(IReadOnlyList<MetricResult> Metrics)
{
    /// <summary>Whether any gated metric regressed past its budget (the process should exit non-zero).</summary>
    public bool HasGatedFailure => Metrics.Any(m => m.IsGatedFailure);
}

/// <summary>Evaluates measurements against the budget (TASK-015). Pure and unit-testable.</summary>
public static class PerfBudgetEvaluator
{
    /// <summary>Builds the per-metric report. A metric is gated when its name is in <see cref="PerfBudget.Gated"/>.</summary>
    public static PerfReport Evaluate(PerfBudget budget, PerfMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(measurement);

        bool IsGated(string name) =>
            budget.Gated.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        var metrics = new List<MetricResult>
        {
            new("coldStart", measurement.ColdStartMs, budget.ColdStartMs, IsGated("coldStart")),
            new("idleRam", measurement.IdleRamMb, budget.IdleRamMb, IsGated("idleRam")),
            new("bundle", measurement.BundleMb, budget.BundleMb, IsGated("bundle")),
        };

        return new PerfReport(metrics);
    }
}
