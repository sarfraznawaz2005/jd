using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Security;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// Real macOS Keychain round-trip (TASK-168 AC0). Unlike <see cref="MacOsKeychainSecretStoreTests"/>
/// (a fake <c>IMacKeychainInterop</c>, logic-only), this exercises the real <see cref="MacKeychainInterop"/>
/// P/Invoke path against the actual Security.framework login Keychain. Runtime-guarded (not just
/// <see cref="SupportedOSPlatformAttribute"/>-gated) because the CI matrix builds and would otherwise try to
/// run this assembly on every OS; selected in CI via <c>dotnet test --filter "Category=RealSecretStore"</c>
/// (<c>.github/workflows/verify-secret-stores.yml</c>), which only runs on a macOS runner — real macOS
/// hardware in GitHub's cloud, satisfying TASK-168 AC0 without needing local Apple hardware (Apple's terms
/// prohibit macOS virtualization on non-Apple hardware, so this is the only way to get real coverage from a
/// Windows dev box).
/// </summary>
[Trait("Category", "RealSecretStore")]
public sealed class MacOsKeychainSecretStoreRealTests
{
    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task StoreRetrieveDelete_RoundTrips_AgainstTheRealLoginKeychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadCiTest-" + Guid.NewGuid().ToString("N"));
        var store = new MacOsKeychainSecretStore(appInfo, new MacKeychainInterop());
        const string secret = "jd-ci-real-keychain-round-trip-secret";

        string secretRef = await store.StoreAsync(secret);
        try
        {
            string? retrieved = await store.RetrieveAsync(secretRef);
            retrieved.Should().Be(secret, "the real Keychain must return exactly what was stored");
        }
        finally
        {
            bool deleted = await store.DeleteAsync(secretRef);
            deleted.Should().BeTrue("the item that was just stored must exist to delete");
        }

        string? afterDelete = await store.RetrieveAsync(secretRef);
        afterDelete.Should().BeNull("the item must be gone from the real Keychain after deletion");
    }
}
