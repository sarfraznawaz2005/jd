using System.Collections.Frozen;
using System.Globalization;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The pure, deterministic transition rules for a download's lifecycle (TASK-031). Centralizing the legal
/// transitions makes illegal states unrepresentable in practice: every status change in the engine goes
/// through <see cref="EnsureCanTransition"/>, so e.g. a completed download can never silently flip back to
/// active and a paused one can only resume or be re-queued. The table is the single source of truth and is
/// covered directly by unit tests.
/// </summary>
public static class DownloadStateMachine
{
    // Adjacency: each state maps to the states it may legally move to. Self-transitions are not listed
    // (a redundant "start an active download" is a caller bug, not a no-op). Completed is terminal.
    private static readonly FrozenDictionary<DownloadStatus, FrozenSet<DownloadStatus>> Allowed =
        new Dictionary<DownloadStatus, FrozenSet<DownloadStatus>>
        {
            [DownloadStatus.Queued] = new[] { DownloadStatus.Active, DownloadStatus.Failed }.ToFrozenSet(),
            [DownloadStatus.Active] = new[]
            {
                DownloadStatus.Paused, DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Expired,
            }.ToFrozenSet(),
            [DownloadStatus.Paused] = new[]
            {
                DownloadStatus.Active, DownloadStatus.Queued, DownloadStatus.Failed,
            }.ToFrozenSet(),
            // Failed and Expired are recoverable: retry/renew re-activates or re-queues them.
            [DownloadStatus.Failed] = new[] { DownloadStatus.Active, DownloadStatus.Queued }.ToFrozenSet(),
            [DownloadStatus.Expired] = new[] { DownloadStatus.Active, DownloadStatus.Queued }.ToFrozenSet(),
            [DownloadStatus.Completed] = FrozenSet<DownloadStatus>.Empty,
        }.ToFrozenDictionary();

    /// <summary>A terminal state has no outgoing transitions; only <see cref="DownloadStatus.Completed"/> is.</summary>
    public static bool IsTerminal(DownloadStatus status) => Allowed[status].Count == 0;

    /// <summary>Returns whether moving from <paramref name="from"/> to <paramref name="to"/> is legal.</summary>
    public static bool CanTransition(DownloadStatus from, DownloadStatus to) => Allowed[from].Contains(to);

    /// <summary>The set of states reachable from <paramref name="from"/> in one step.</summary>
    public static IReadOnlyCollection<DownloadStatus> NextStates(DownloadStatus from) => Allowed[from];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="from"/> → <paramref name="to"/> is
    /// not a legal transition; otherwise returns <paramref name="to"/> for fluent use.
    /// </summary>
    public static DownloadStatus EnsureCanTransition(DownloadStatus from, DownloadStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Illegal download transition: {from} → {to}."));
        }

        return to;
    }
}
