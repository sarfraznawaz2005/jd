using FluentAssertions;
using JustDownload.App.ViewModels.Settings;
using JustDownload.Core.Security;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Authentication settings view-model (TASK-126): it lists the saved credentials by description (never the
/// secret) and revokes one through the service, then refreshes.
/// </summary>
public sealed class AuthenticationSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_PopulatesRows_FromTheService()
    {
        var service = Substitute.For<ISavedCredentialsService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SavedCredential(SavedCredentialKind.GlobalProxyPassword, "Proxy password for proxy.local", null),
            new SavedCredential(SavedCredentialKind.DownloadCookies, "Cookies for download https://site", 7),
        });
        var vm = new AuthenticationSettingsViewModel(service);

        await vm.LoadAsync();

        vm.Credentials.Select(c => c.Description).Should()
            .Contain("Proxy password for proxy.local").And.Contain("Cookies for download https://site");
        vm.HasNoSavedCredentials.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_NoCredentials_ShowsEmptyState()
    {
        var service = Substitute.For<ISavedCredentialsService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var vm = new AuthenticationSettingsViewModel(service);

        await vm.LoadAsync();

        vm.Credentials.Should().BeEmpty();
        vm.HasNoSavedCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_RevokesThroughService_AndReloads()
    {
        var credential = new SavedCredential(SavedCredentialKind.GlobalProxyPassword, "Proxy password", null);
        var service = Substitute.For<ISavedCredentialsService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([credential]);
        var vm = new AuthenticationSettingsViewModel(service);
        await vm.LoadAsync();
        vm.Credentials.Should().ContainSingle();

        // After removal the service reports nothing saved; the command revokes then refreshes.
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        await vm.RemoveCommand.ExecuteAsync(new SavedCredentialRow(credential));

        await service.Received(1).RemoveAsync(credential, Arg.Any<CancellationToken>());
        vm.Credentials.Should().BeEmpty("the list refreshes after removal");
    }

    /// <summary>
    /// Revoking one at a time is unworkable once a handful of downloads have each saved cookies
    /// (user-reported: 19 rows, 19 clicks), so the panel offers a single bulk revoke.
    /// </summary>
    [Fact]
    public async Task RemoveAll_RevokesEveryCredential_ThenReloads()
    {
        SavedCredential[] saved =
        [
            new(SavedCredentialKind.GlobalProxyPassword, "Proxy password", null),
            new(SavedCredentialKind.DownloadCookies, "Cookies for a.bin — https://site", 1),
            new(SavedCredentialKind.DownloadCookies, "Cookies for b.bin — https://site", 2),
        ];
        var service = Substitute.For<ISavedCredentialsService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns(saved);
        var vm = new AuthenticationSettingsViewModel(service);
        await vm.LoadAsync();
        vm.CanRemoveAll.Should().BeTrue();
        vm.RemoveAllLabel.Should().Be("Remove all 3", "the count makes the click's scope unambiguous");

        service.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        await vm.RemoveAllCommand.ExecuteAsync(null);

        foreach (SavedCredential credential in saved)
        {
            await service.Received(1).RemoveAsync(credential, Arg.Any<CancellationToken>());
        }

        vm.Credentials.Should().BeEmpty();
        vm.CanRemoveAll.Should().BeFalse();
        vm.HasNoSavedCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAll_IsNotOffered_ForASingleCredential()
    {
        var service = Substitute.For<ISavedCredentialsService>();
        service.ListAsync(Arg.Any<CancellationToken>())
            .Returns([new SavedCredential(SavedCredentialKind.GlobalProxyPassword, "Proxy password", null)]);
        var vm = new AuthenticationSettingsViewModel(service);

        await vm.LoadAsync();

        vm.CanRemoveAll.Should().BeFalse("a lone credential already has its own Remove button");
    }
}
