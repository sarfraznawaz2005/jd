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

    /// <summary>
    /// Whether a download in <paramref name="status"/> may be restarted from scratch — the explicit
    /// "re-download" action, which discards the checkpoint and the file on disk and fetches the resource
    /// again from byte zero.
    /// <para>
    /// A restart is deliberately <b>not</b> modelled as a transition in <see cref="Allowed"/>. It is a reset
    /// rather than a step through the lifecycle: it ends the download's current life and begins a new one at
    /// <see cref="DownloadStatus.Queued"/>. Keeping it out of the table preserves the property that
    /// <see cref="DownloadStatus.Completed"/> is terminal — a finished download still cannot *drift* back to
    /// active through any ordinary transition — while still allowing the user to ask for a fresh fetch.
    /// </para>
    /// Everything except <see cref="DownloadStatus.Active"/> qualifies; an in-flight transfer must be paused
    /// first so its workers and file handles are released before the destination is deleted.
    /// </summary>
    public static bool CanRestart(DownloadStatus status) => status != DownloadStatus.Active;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if a download in <paramref name="status"/> cannot be
    /// restarted; otherwise returns <see cref="DownloadStatus.Queued"/>, the state a restart resets to.
    /// </summary>
    public static DownloadStatus EnsureCanRestart(DownloadStatus status)
    {
        if (!CanRestart(status))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cannot restart a download that is {status}; pause it first."));
        }

        return DownloadStatus.Queued;
    }

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
