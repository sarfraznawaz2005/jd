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
}
