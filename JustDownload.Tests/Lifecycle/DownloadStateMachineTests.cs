using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Unit tests for the pure lifecycle transition rules (TASK-031 AC0): legal moves are allowed, illegal ones
/// throw, and <see cref="DownloadStatus.Completed"/> is the only terminal state.
/// </summary>
public sealed class DownloadStateMachineTests
{
    [Theory]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Active)]
    [InlineData(DownloadStatus.Active, DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Active, DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Active, DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Active, DownloadStatus.Expired)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Active)]
    [InlineData(DownloadStatus.Failed, DownloadStatus.Active)]
    [InlineData(DownloadStatus.Expired, DownloadStatus.Active)]
    public void CanTransition_AllowsLegalMoves(DownloadStatus from, DownloadStatus to)
    {
        DownloadStateMachine.CanTransition(from, to).Should().BeTrue();
        DownloadStateMachine.EnsureCanTransition(from, to).Should().Be(to);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Active)]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Active, DownloadStatus.Active)]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Queued)]
    public void CanTransition_RejectsIllegalMoves(DownloadStatus from, DownloadStatus to)
    {
        DownloadStateMachine.CanTransition(from, to).Should().BeFalse();

        Action act = () => DownloadStateMachine.EnsureCanTransition(from, to);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Illegal download transition*");
    }

    [Fact]
    public void IsTerminal_OnlyCompletedIsTerminal()
    {
        DownloadStateMachine.IsTerminal(DownloadStatus.Completed).Should().BeTrue();

        foreach (DownloadStatus status in Enum.GetValues<DownloadStatus>())
        {
            if (status != DownloadStatus.Completed)
            {
                DownloadStateMachine.IsTerminal(status).Should().BeFalse($"{status} is recoverable");
            }
        }
    }

    [Fact]
    public void NextStates_OfTerminal_IsEmpty()
    {
        DownloadStateMachine.NextStates(DownloadStatus.Completed).Should().BeEmpty();
    }

    /// <summary>
    /// Re-download resets a download rather than transitioning it, so it is the one way a completed download
    /// runs again — without making Completed non-terminal for any ordinary transition.
    /// </summary>
    [Fact]
    public void CanRestart_EverythingExceptActive_AndLeavesTheTransitionTableAlone()
    {
        DownloadStateMachine.CanRestart(DownloadStatus.Active).Should().BeFalse("its workers still hold the file");

        foreach (DownloadStatus status in Enum.GetValues<DownloadStatus>())
        {
            if (status != DownloadStatus.Active)
            {
                DownloadStateMachine.CanRestart(status).Should().BeTrue($"{status} can be re-downloaded");
                DownloadStateMachine.EnsureCanRestart(status).Should().Be(DownloadStatus.Queued);
            }
        }

        // The reset must not have been smuggled in as a transition: Completed stays terminal.
        DownloadStateMachine.IsTerminal(DownloadStatus.Completed).Should().BeTrue();
        DownloadStateMachine.CanTransition(DownloadStatus.Completed, DownloadStatus.Queued).Should().BeFalse();
    }

    [Fact]
    public void EnsureCanRestart_OnActive_Throws()
    {
        Action act = () => DownloadStateMachine.EnsureCanRestart(DownloadStatus.Active);
        act.Should().Throw<InvalidOperationException>().WithMessage("*pause it first*");
    }
}
