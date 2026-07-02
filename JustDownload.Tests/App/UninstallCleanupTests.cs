using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The Windows installer's uninstall custom action hook (TASK-076): <c>JustDownload.App.exe --uninstall-cleanup</c>
/// must remove this install's native-messaging host registrations without touching Avalonia/the GUI, so the
/// MSI's silent uninstall never orphans registry entries or manifest files pointing at a deleted executable.
/// </summary>
public sealed class UninstallCleanupTests
{
    [Fact]
    public void Run_UninstallsTheNativeHost_AndReturnsZero()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        var services = new ServiceCollection().AddSingleton(installer).BuildServiceProvider();

        int exitCode = UninstallCleanup.Run(services);

        exitCode.Should().Be(0);
        installer.Received(1).Uninstall();
    }

    [Fact]
    public void Run_ReturnsNonZero_WhenUninstallThrowsAnIoException()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.When(i => i.Uninstall()).Do(_ => throw new IOException("locked"));
        var services = new ServiceCollection().AddSingleton(installer).BuildServiceProvider();

        int exitCode = UninstallCleanup.Run(services);

        exitCode.Should().Be(1, "a cleanup failure must be surfaced, not silently swallowed (§1 no silent failures)");
    }

    [Fact]
    public void Run_NullServices_Throws()
    {
        Action act = () => UninstallCleanup.Run(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
