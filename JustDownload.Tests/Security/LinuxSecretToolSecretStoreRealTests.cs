using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Security;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// Real Linux Secret Service round-trip (TASK-168 AC1). Unlike <see cref="LinuxSecretToolSecretStoreTests"/>
/// (a scripted <c>Fixtures/fake-secret-tool.sh</c> stand-in), this invokes the real <c>secret-tool</c> binary
/// against a real Secret Service provider (GNOME Keyring/KWallet). That requires libsecret-tools installed
/// plus a running, unlocked D-Bus Secret Service session — there is no such session on a normal headless dev
/// box, so this is CI/real-hardware-only by nature, not just OS-gated. CI sets one up via
/// <c>dbus-run-session</c> + <c>gnome-keyring-daemon --unlock</c> before running
/// <c>dotnet test --filter "Category=RealSecretStore"</c> (<c>.github/workflows/verify-secret-stores.yml</c>).
/// </summary>
[Trait("Category", "RealSecretStore")]
public sealed class LinuxSecretToolSecretStoreRealTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task StoreRetrieveDelete_RoundTrips_AgainstTheRealSecretService()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadCiTest-" + Guid.NewGuid().ToString("N"));
        var store = new LinuxSecretToolSecretStore(appInfo);
        const string secret = "jd-ci-real-secret-service-round-trip-secret";

        string secretRef = await store.StoreAsync(secret);
        try
        {
            string? retrieved = await store.RetrieveAsync(secretRef);
            retrieved.Should().Be(secret, "the real Secret Service must return exactly what was stored");
        }
        finally
        {
            bool deleted = await store.DeleteAsync(secretRef);
            deleted.Should().BeTrue("the item that was just stored must exist to delete");
        }

        string? afterDelete = await store.RetrieveAsync(secretRef);
        afterDelete.Should().BeNull("the item must be gone from the real Secret Service after deletion");
    }
}
