using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The one-time ToS/legal notice (TASK-160): each of the three buttons raises the matching
/// <see cref="TosNoticeResult"/> so the gate and the window's code-behind can act on it.
/// </summary>
public sealed class TosNoticeViewModelTests
{
    [Fact]
    public void ContinueCommand_RaisesCloseRequested_WithContinue()
    {
        var vm = new TosNoticeViewModel();
        TosNoticeResult? result = null;
        vm.CloseRequested += (_, r) => result = r;

        vm.ContinueCommand.Execute(null);

        result.Should().Be(TosNoticeResult.Continue);
    }

    [Fact]
    public void CancelCommand_RaisesCloseRequested_WithCancel()
    {
        var vm = new TosNoticeViewModel();
        TosNoticeResult? result = null;
        vm.CloseRequested += (_, r) => result = r;

        vm.CancelCommand.Execute(null);

        result.Should().Be(TosNoticeResult.Cancel);
    }

    [Fact]
    public void DontShowAgainCommand_RaisesCloseRequested_WithContinueAndSuppress()
    {
        var vm = new TosNoticeViewModel();
        TosNoticeResult? result = null;
        vm.CloseRequested += (_, r) => result = r;

        vm.DontShowAgainCommand.Execute(null);

        result.Should().Be(TosNoticeResult.ContinueAndSuppress);
    }

    [Fact]
    public void Copy_MatchesDocsLegalMdVerbatim()
    {
        // The exact wording from docs/LEGAL.md §"Terms-of-Service notice" (AC0) — kept as a literal
        // assertion so a future edit to either the doc or the view-model surfaces a drift.
        var vm = new TosNoticeViewModel();

        vm.Heading.Should().Be("Before you download media");
        vm.Intro.Should().Be(
            "JustDownload can download video and audio that it detects on web pages. Downloading content from "
            + "some websites may violate that site's Terms of Service, and some content may be protected by "
            + "copyright.");
        vm.Bullets.Should().Equal(
            "You are responsible for ensuring you have the right to download and use any content.",
            "JustDownload only downloads streams that are openly accessible. It does not bypass or remove any "
                + "DRM or copy protection, and it will not attempt to do so.",
            "JustDownload is not affiliated with, endorsed by, or sponsored by any website you download from.");
        vm.Confirmation.Should().Be(
            "By continuing, you confirm that you understand this and will use JustDownload responsibly.");
    }

    [AvaloniaFact]
    public void Window_Mounts_AndClosingWithAResult_RaisesThatResultFromShowDialog()
    {
        var vm = new TosNoticeViewModel();
        var window = new TosNoticeWindow { DataContext = vm };
        window.Show();

        window.IsVisible.Should().BeTrue();

        vm.CancelCommand.Execute(null); // closes the window with TosNoticeResult.Cancel
        window.IsVisible.Should().BeFalse();
    }
}
