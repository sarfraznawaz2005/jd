using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.NativeMessaging.Registration;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The Browsers panel view-model (TASK-093): it surfaces per-browser registration status and connects /
/// disconnects browser integration through <see cref="INativeHostInstaller"/>.
/// </summary>
public sealed class BrowsersViewModelTests
{
    [Fact]
    public void Ctor_PopulatesPerBrowserStatus()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        installer.GetStatus().Returns(new[]
        {
            new BrowserRegistrationStatus(NativeMessagingBrowser.Chrome, true),
            new BrowserRegistrationStatus(NativeMessagingBrowser.Firefox, false),
        });

        var vm = new BrowsersViewModel(installer);

        vm.HostAvailable.Should().BeTrue();
        vm.Browsers.Should().HaveCount(2);
        vm.Browsers.Single(b => b.Name == "Chrome").StatusText.Should().Be("Connected");
        vm.Browsers.Single(b => b.Name == "Firefox").StatusText.Should().Be("Not connected");
    }

    [Fact]
    public void Register_Installs_AndReportsConnected()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        installer.Install().Returns(true);
        installer.GetStatus().Returns([]);
        var vm = new BrowsersViewModel(installer);

        vm.RegisterCommand.Execute(null);

        installer.Received(1).Install();
        vm.StatusMessage.Should().Contain("Connected");
    }

    [Fact]
    public void Register_WhenHostMissing_ReportsFailure()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.Install().Returns(false);
        installer.GetStatus().Returns([]);
        var vm = new BrowsersViewModel(installer);

        vm.RegisterCommand.Execute(null);

        vm.StatusMessage.Should().Contain("wasn't found");
    }

    [Fact]
    public void Unregister_CallsInstaller_AndReportsDisconnected()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.GetStatus().Returns([]);
        var vm = new BrowsersViewModel(installer);

        vm.UnregisterCommand.Execute(null);

        installer.Received(1).Uninstall();
        vm.StatusMessage.Should().Contain("Disconnected");
    }
}
