using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Perf;
using Microsoft.Extensions.DependencyInjection;

// Performance-budget probe (TASK-015). Measures engine cold-start, idle RAM, and (when --bundle is given)
// the published bundle size, compares them to perf-budget.json, prints a report, and exits non-zero only
// when a *gated* KPI (the bundle size) regresses.
//
// --bundle accepts either the compressed installer artifact (a .zip — the K3 "installer/bundle" metric,
// TASK-075) or a publish directory (its files are summed). Prefer the installer: that is what users download.

string? bundlePath = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--bundle")
    {
        bundlePath = args[i + 1];
    }
}

// --- Cold-start: time building the engine and running its async startup. -------------------------
string tempDir = Path.Combine(Path.GetTempPath(), "jd-perf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
double coldStartMs;
try
{
    var stopwatch = Stopwatch.StartNew();
    var services = new ServiceCollection();
    services.AddSingleton<IDatabasePathProvider>(new TempDatabasePathProvider(tempDir));
    services.AddJustDownloadCore();
    await using ServiceProvider provider = services.BuildServiceProvider();
    await provider.InitializeJustDownloadCoreAsync();
    stopwatch.Stop();
    coldStartMs = stopwatch.Elapsed.TotalMilliseconds;
}
finally
{
    try
    {
        Directory.Delete(tempDir, recursive: true);
    }
    catch (IOException)
    {
    }
}

// --- Idle RAM: working set after a full collect. ------------------------------------------------
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
double idleRamMb = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;

// --- Bundle: size of the compressed installer (a file) or a publish directory, when provided. ----
double? bundleMb = null;
if (bundlePath is not null && File.Exists(bundlePath))
{
    bundleMb = new FileInfo(bundlePath).Length / 1024.0 / 1024.0;
}
else if (bundlePath is not null && Directory.Exists(bundlePath))
{
    long bytes = Directory.EnumerateFiles(bundlePath, "*", SearchOption.AllDirectories)
        .Sum(f => new FileInfo(f).Length);
    bundleMb = bytes / 1024.0 / 1024.0;
}

// --- Evaluate against the budget and report. ----------------------------------------------------
PerfBudget budget = LoadBudget();
var measurement = new PerfMeasurement(coldStartMs, idleRamMb, bundleMb);
PerfReport report = PerfBudgetEvaluator.Evaluate(budget, measurement);

Console.WriteLine("Performance budget (TASK-015):");
foreach (MetricResult m in report.Metrics)
{
    string measured = m.Measured is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
    string status = m.Measured is null ? "skipped" : m.WithinBudget ? "ok" : m.Gated ? "FAIL" : "over (tracked)";
    Console.WriteLine(
        string.Create(CultureInfo.InvariantCulture,
            $"  {m.Name,-10} {measured,8} / {m.Limit,6} {(m.Gated ? "[gated]" : "[tracked]"),-9} {status}"));
}

if (report.HasGatedFailure)
{
    Console.Error.WriteLine("A gated KPI regressed past its budget.");
    return 1;
}

return 0;

static PerfBudget LoadBudget()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "perf-budget.json")))
    {
        dir = dir.Parent;
    }

    string path = Path.Combine(dir?.FullName ?? ".", "perf-budget.json");
    using FileStream stream = File.OpenRead(path);
    using JsonDocument doc = JsonDocument.Parse(stream);
    JsonElement limits = doc.RootElement.GetProperty("limits");
    string[] gated = doc.RootElement.TryGetProperty("gated", out JsonElement g) && g.ValueKind == JsonValueKind.Array
        ? g.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
        : [];

    return new PerfBudget(
        limits.GetProperty("coldStartMs").GetInt32(),
        limits.GetProperty("idleRamMb").GetInt32(),
        limits.GetProperty("bundleMb").GetInt32(),
        gated);
}

internal sealed class TempDatabasePathProvider : IDatabasePathProvider
{
    public TempDatabasePathProvider(string directory)
    {
        DatabaseDirectory = directory;
        DatabasePath = Path.Combine(directory, "perf.db");
    }

    public string DatabaseDirectory { get; }

    public string DatabasePath { get; }
}
