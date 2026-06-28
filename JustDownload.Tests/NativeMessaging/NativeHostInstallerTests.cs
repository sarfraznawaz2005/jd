using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.NativeMessaging.Registration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// The startup native-host installer (TASK-089): it registers the host (so browsers can find/launch it) when
/// the host executable ships next to the app, and skips registration rather than pointing browsers at a
/// missing binary otherwise. The file-system probes are injected so the decision is unit-testable.
/// </summary>
public sealed class NativeHostInstallerTests
{
    private static IAppInfoProvider AppInfo()
    {
        var info = Substitute.For<IAppInfoProvider>();
        info.Name.Returns("JustDownload");
        return info;
    }

    [Fact]
    public void Install_RegistersHost_WithIdentityDerivedRegistration_WhenExecutablePresent()
    {
        var registrar = Substitute.For<INativeHostRegistrar>();
        const string exe = @"C:\Program Files\JustDownload\JustDownload.NativeHost.exe";
        var installer = new NativeHostInstaller(
            registrar, AppInfo(), () => exe, _ => true, NullLogger<NativeHostInstaller>.Instance);

        bool ran = installer.Install();

        ran.Should().BeTrue();
        registrar.Received(1).Register(Arg.Is<NativeHostRegistration>(r =>
            r.Name == NativeHostIdentity.HostName &&
            r.ExecutablePath == exe &&
            r.AllowedExtensionIds.Contains(NativeHostIdentity.FirefoxExtensionId)));
    }

    [Fact]
    public void Install_Skips_WhenExecutableMissing()
    {
        var registrar = Substitute.For<INativeHostRegistrar>();
        var installer = new NativeHostInstaller(
            registrar, AppInfo(), () => @"C:\missing\JustDownload.NativeHost.exe", _ => false,
            NullLogger<NativeHostInstaller>.Instance);

        installer.Install().Should().BeFalse("registration must not point browsers at a missing binary");
        registrar.DidNotReceive().Register(Arg.Any<NativeHostRegistration>());
    }

    [Fact]
    public void Uninstall_RemovesTheHost()
    {
        var registrar = Substitute.For<INativeHostRegistrar>();
        var installer = new NativeHostInstaller(
            registrar, AppInfo(), () => "host", _ => true, NullLogger<NativeHostInstaller>.Instance);

        installer.Uninstall();

        registrar.Received(1).Unregister(NativeHostIdentity.HostName);
    }
}
