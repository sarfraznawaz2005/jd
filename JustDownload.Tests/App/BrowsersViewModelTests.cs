using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.Core.NativeMessaging;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The Browsers panel view-model (TASK-093, TASK-175): it surfaces per-browser-family connection status
/// (real observed contact via <see cref="IExtensionContactTracker"/>, not just manifest registration) and
/// connects/disconnects browser integration through <see cref="INativeHostInstaller"/>.
/// </summary>
public sealed class BrowsersViewModelTests
{
    private static IExtensionContactTracker Tracker(DateTimeOffset? chromium = null, DateTimeOffset? firefox = null)
    {
        var tracker = Substitute.For<IExtensionContactTracker>();
        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Returns(chromium);
        tracker.GetLastContact(ExtensionContactOrigin.Firefox).Returns(firefox);
        return tracker;
    }

    [Fact]
    public void Ctor_PopulatesPerBrowserFamilyConnectionStatus_FromRealObservedContact()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        IExtensionContactTracker tracker = Tracker(chromium: DateTimeOffset.UtcNow, firefox: null);

        var vm = new BrowsersViewModel(installer, tracker, Substitute.For<JustDownload.Core.IPortableEnvironment>());

        vm.HostAvailable.Should().BeTrue();
        vm.Browsers.Should().HaveCount(2);
        vm.Browsers.Single(b => b.Name.Contains("Chromium")).StatusText.Should().Be("Connected");
        vm.Browsers.Single(b => b.Name == "Firefox").StatusText.Should().Be("Not connected");
    }

    [Fact]
    public void Ctor_ReportsNotConnected_ForEveryBrowser_WhenNoContactHasEverBeenObserved()
    {
        // The exact bug this guards against: a manifest file existing (written on every app startup
        // regardless of whether any extension is installed) must never be mistaken for a real connection.
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        var vm = new BrowsersViewModel(installer, Tracker(), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        vm.Browsers.Should().OnlyContain(b => b.StatusText == "Not connected");
    }

    [Fact]
    public void InPortableMode_RegistrationIsDisabled_AndNeverWritesTheRegistry()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        var portable = Substitute.For<JustDownload.Core.IPortableEnvironment>();
        portable.IsPortable.Returns(true);
        var vm = new BrowsersViewModel(installer, Tracker(), portable);

        vm.IsPortable.Should().BeTrue();
        vm.CanManageRegistration.Should().BeFalse();
        vm.RegisterCommand.CanExecute(null).Should().BeFalse();
        vm.UnregisterCommand.CanExecute(null).Should().BeFalse();
        vm.StatusMessage.Should().Contain("portable mode");

        vm.RegisterCommand.Execute(null); // even if invoked, it must not install
        installer.DidNotReceive().Install();
    }

    [Fact]
    public void Register_Installs_AndReportsRegisteredNotConnected()
    {
        // Registering only makes the host reachable — it must not claim a connection that hasn't
        // happened yet (TASK-175: that overclaim was the original bug).
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        installer.Install().Returns(true);
        var vm = new BrowsersViewModel(installer, Tracker(), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        vm.RegisterCommand.Execute(null);

        installer.Received(1).Install();
        vm.StatusMessage.Should().Contain("registered").And.NotContain("Connected");
    }

    [Fact]
    public void Register_WhenHostMissing_ReportsFailure()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.Install().Returns(false);
        var vm = new BrowsersViewModel(installer, Tracker(), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        vm.RegisterCommand.Execute(null);

        vm.StatusMessage.Should().Contain("wasn't found");
    }

    [Fact]
    public void Unregister_CallsInstaller_AndReportsDisconnected()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        var vm = new BrowsersViewModel(installer, Tracker(), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        vm.UnregisterCommand.Execute(null);

        installer.Received(1).Uninstall();
        vm.StatusMessage.Should().Contain("Disconnected");
    }
}
