using FluentAssertions;
using JustDownload.App.ViewModels.Settings;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Settings;
using JustDownload.Core.Updates;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Updates settings status reporting (TASK-080, error styling reworked per a user-reported clipped error
/// message on a real screenshot): <see cref="UpdateSettingsViewModel.IsStatusError"/> distinguishes failures
/// (network error, security rejections) from neutral/success outcomes, driving the red-banner treatment in
/// the view instead of every status sharing the same quiet dim-text style. Also covers TASK-223's two-step
/// confirm-before-download flow: "Update Now" only becomes available after a signed update is detected,
/// cancelling leaves the pending update intact, and a successful apply raises <c>QuitRequested</c>.
/// </summary>
public sealed class UpdateSettingsViewModelTests
{
    private static UpdateSettingsViewModel Build(UpdateCheckResult result)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { AutoUpdateEnabled = true });
        var checker = Substitute.For<IUpdateChecker>();
        checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));
        var versionProvider = Substitute.For<IAppVersionProvider>();
        versionProvider.CurrentVersion.Returns("1.0.0");

        return new UpdateSettingsViewModel(settings, checker, versionProvider);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.Error)]
    [InlineData(UpdateCheckStatus.RejectedUnsigned)]
    [InlineData(UpdateCheckStatus.RejectedInvalidSignature)]
    [InlineData(UpdateCheckStatus.RejectedAssetHashMismatch)]
    public async Task IsStatusError_TrueForFailureOutcomes(UpdateCheckStatus status)
    {
        UpdateSettingsViewModel vm = Build(new UpdateCheckResult(status, ErrorMessage: "a connection attempt failed"));

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.IsStatusError.Should().BeTrue();
        vm.StatusText.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(UpdateCheckStatus.UpToDate)]
    [InlineData(UpdateCheckStatus.AvailableForManualDownload)]
    [InlineData(UpdateCheckStatus.Applied)]
    [InlineData(UpdateCheckStatus.NotConfigured)]
    [InlineData(UpdateCheckStatus.Disabled)]
    public async Task IsStatusError_FalseForNeutralOrSuccessOutcomes(UpdateCheckStatus status)
    {
        UpdateSettingsViewModel vm = Build(new UpdateCheckResult(status, LatestVersion: "1.0.1"));

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public void IsStatusError_FalseBeforeAnyCheckHasRun()
    {
        UpdateSettingsViewModel vm = Build(new UpdateCheckResult(UpdateCheckStatus.UpToDate));

        vm.IsStatusError.Should().BeFalse();
        vm.StatusText.Should().BeEmpty();
    }

    [Fact]
    public async Task StatusText_ForError_IncludesTheFullErrorMessage()
    {
        // Real-world case (user-reported): a truncated-looking connection error in the view is a layout bug,
        // not the view model dropping text -- StatusText itself must carry the whole message.
        const string fullMessage = "A connection attempt failed because the connected party did not " +
            "properly respond after a period of time, or established connection failed because connected " +
            "host has failed to respond.";
        UpdateSettingsViewModel vm = Build(new UpdateCheckResult(UpdateCheckStatus.Error, ErrorMessage: fullMessage));

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.StatusText.Should().Contain(fullMessage);
    }

    private static UpdateCheckResult AvailableResult(string version = "1.0.1") => new(
        UpdateCheckStatus.Available, version, InstallerAssetName: "JustDownload-win-x64-Setup.exe",
        InstallerDownloadUrl: new Uri("https://example.com/setup.exe"), ExpectedSha256: new string('a', 64));

    private static (UpdateSettingsViewModel Vm, IUpdateChecker Checker) BuildWithChecker(UpdateCheckResult checkResult)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { AutoUpdateEnabled = true });
        var checker = Substitute.For<IUpdateChecker>();
        checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(checkResult));
        var versionProvider = Substitute.For<IAppVersionProvider>();
        versionProvider.CurrentVersion.Returns("1.0.0");

        return (new UpdateSettingsViewModel(settings, checker, versionProvider), checker);
    }

    [Fact]
    public void Constructor_PrePopulatesState_FromCheckerLastResult()
    {
        // The one-shot startup check (TASK-223) runs before Settings is ever opened; its result must show
        // immediately, without requiring another manual "Check for Updates" click.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { AutoUpdateEnabled = true });
        var checker = Substitute.For<IUpdateChecker>();
        checker.LastResult.Returns(AvailableResult("1.2.3"));
        var versionProvider = Substitute.For<IAppVersionProvider>();
        versionProvider.CurrentVersion.Returns("1.0.0");

        var vm = new UpdateSettingsViewModel(settings, checker, versionProvider);

        vm.IsUpdateAvailable.Should().BeTrue();
        vm.LatestVersion.Should().Be("1.2.3");
    }

    [Fact]
    public void UpdateNowCommand_CannotExecute_BeforeACheckFindsAnUpdate()
    {
        UpdateSettingsViewModel vm = Build(new UpdateCheckResult(UpdateCheckStatus.UpToDate));

        vm.UpdateNowCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateNowCommand_BecomesExecutable_OnceCheckReportsAvailable()
    {
        (UpdateSettingsViewModel vm, _) = BuildWithChecker(AvailableResult());

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.UpdateNowCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateNowCommand_OnApplied_RaisesQuitRequested_AfterTheConfirmationMessage()
    {
        (UpdateSettingsViewModel vm, IUpdateChecker checker) = BuildWithChecker(AvailableResult());
        checker.DownloadAndApplyAsync(Arg.Any<UpdateCheckResult>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Applied, "1.0.1")));
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        bool quitRaised = false;
        vm.QuitRequested += (_, _) => quitRaised = true;

        await vm.UpdateNowCommand.ExecuteAsync(null);

        quitRaised.Should().BeTrue("the app must quit once the installer has launched (TASK-223)");
        vm.IsDownloading.Should().BeFalse();
        vm.StatusText.Should().Be("Installer launched — closing JustDownload…");
    }

    [Fact]
    public async Task CancelDownloadCommand_CancelsInProgressDownload_AndKeepsUpdateAvailable()
    {
        (UpdateSettingsViewModel vm, IUpdateChecker checker) = BuildWithChecker(AvailableResult());
        checker.DownloadAndApplyAsync(Arg.Any<UpdateCheckResult>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => NeverCompletesAsync(callInfo.Arg<CancellationToken>()));
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Task updateTask = vm.UpdateNowCommand.ExecuteAsync(null);
        vm.IsDownloading.Should().BeTrue("IsDownloading flips synchronously before the download's first await");

        vm.CancelDownloadCommand.Execute(null);
        await updateTask;

        vm.IsDownloading.Should().BeFalse();
        vm.IsUpdateAvailable.Should().BeTrue(
            "a user-cancelled download must leave the pending update available, not look like a failure");
    }

    private static async Task<UpdateCheckResult> NeverCompletesAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable — Task.Delay(Timeout.Infinite, ct) only returns via cancellation.");
    }
}
