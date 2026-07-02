using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Security;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// Runtime round-trip coverage for <see cref="LinuxSecretToolSecretStore"/> (TASK-113 AC0) against a
/// scripted stand-in for the real <c>secret-tool</c> binary (<c>Fixtures/fake-secret-tool.sh</c>) —
/// libsecret and a Secret Service daemon aren't installable on this box. This proves the store's
/// actual <c>Process.Start</c>/stdin-write/stdout-read/exit-code logic end-to-end against a fixture
/// that mimics <c>secret-tool</c>'s I/O contract; it is not a real Secret Service backend. Runs only
/// on Linux (early-return guard, matching the DPAPI tests' pattern) — the fixture is a POSIX shell
/// script invoked directly (via its shebang), which needs the Linux-only executable bit and
/// <c>/bin/sh</c>. The injectable <see cref="LinuxSecretToolSecretStore"/> constructor (added by
/// TASK-113) is what lets the test point the store at the fixture instead of the real binary.
/// </summary>
public sealed class LinuxSecretToolSecretStoreTests : IDisposable
{
    private const string SampleSecret = "hunter2-Sup3r!Secret-Pa$$w0rd-7f3a9c";

    private readonly string _vaultDir;
    private readonly string _scriptPath;

    public LinuxSecretToolSecretStoreTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "jd-fake-secret-tool-vault-" + Guid.NewGuid().ToString("N"));
        _scriptPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-secret-tool.sh");
        Environment.SetEnvironmentVariable("FAKE_SECRET_TOOL_VAULT", _vaultDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FAKE_SECRET_TOOL_VAULT", null);
        try
        {
            Directory.Delete(_vaultDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever stored — the vault directory was never created.
        }
        catch (IOException)
        {
            // A lingering handle on a CI runner shouldn't fail the suite; the temp dir is reaped by the OS.
        }
    }

    [SupportedOSPlatform("linux")]
    private LinuxSecretToolSecretStore Store()
    {
        // The fixture ships without the executable bit (Git/CopyToOutputDirectory don't preserve it),
        // so it must be set before the first invocation. Every caller below early-returns unless
        // OperatingSystem.IsLinux(), matching this method's own platform annotation.
        File.SetUnixFileMode(
            _scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownload");
        return new LinuxSecretToolSecretStore(appInfo, secretToolPath: _scriptPath);
    }

    [Fact]
    public async Task Linux_Store_Then_Retrieve_RoundTrips()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        LinuxSecretToolSecretStore store = Store();

        string secretRef = await store.StoreAsync(SampleSecret);
        string? retrieved = await store.RetrieveAsync(secretRef);

        secretRef.Should().NotBeNullOrEmpty().And.NotBe(SampleSecret);
        retrieved.Should().Be(SampleSecret);
    }

    [Fact]
    public async Task Linux_Retrieve_ReturnsNull_ForUnknownReference()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        LinuxSecretToolSecretStore store = Store();

        (await store.RetrieveAsync("never-stored")).Should().BeNull();
    }

    [Fact]
    public async Task Linux_Delete_RemovesSecret_AndReportsWhetherAnythingExisted()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        LinuxSecretToolSecretStore store = Store();
        string secretRef = await store.StoreAsync(SampleSecret);

        bool deleted = await store.DeleteAsync(secretRef);
        string? afterDelete = await store.RetrieveAsync(secretRef);

        deleted.Should().BeTrue();
        afterDelete.Should().BeNull();
        (await store.DeleteAsync(secretRef)).Should().BeFalse("a second delete finds nothing");
    }
}
