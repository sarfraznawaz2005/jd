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
/// the view instead of every status sharing the same quiet dim-text style.
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
}
