using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Security;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// Unit coverage for <see cref="MacOsKeychainSecretStore"/>'s own logic — secretRef generation, the
/// exact service/account/secret values handed down, and OSStatus-to-outcome mapping — against a fake
/// <see cref="IMacKeychainInterop"/> (TASK-113 AC0). This does not touch Security.framework: the real
/// P/Invoke implementation lives in <see cref="MacKeychainInterop"/> and is not exercised here — see
/// that class's remarks for why it can't be run on this machine. <see cref="SupportedOSPlatformAttribute"/>
/// mirrors the store's own annotation to satisfy the platform-compatibility analyzer (CA1416); unlike
/// the DPAPI tests' <c>if (!OperatingSystem.IsWindows())</c> guard, there is deliberately no runtime
/// skip here, because the fake interop means these tests exercise real logic on any OS.
/// </summary>
public sealed class MacOsKeychainSecretStoreTests
{
    private const string SampleSecret = "hunter2-Sup3r!Secret-Pa$$w0rd-7f3a9c";
    private const string Service = "JustDownload";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int OtherFailure = -25299;

    private readonly IMacKeychainInterop _keychain = Substitute.For<IMacKeychainInterop>();
    private readonly IAppInfoProvider _appInfo = Substitute.For<IAppInfoProvider>();

    public MacOsKeychainSecretStoreTests()
    {
        _appInfo.Name.Returns(Service);
    }

    [SupportedOSPlatform("macos")]
    private MacOsKeychainSecretStore Store() => new(_appInfo, _keychain);

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task StoreAsync_ReturnsFreshOpaqueRef_AndPassesServiceAndSecretToInterop()
    {
        _keychain.Add(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Success);

        string secretRef = await Store().StoreAsync(SampleSecret);

        secretRef.Should().NotBeNullOrEmpty().And.NotBe(SampleSecret);
        _keychain.Received(1).Add(Service, secretRef, SampleSecret);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task StoreAsync_TwoCalls_GetDifferentReferences()
    {
        _keychain.Add(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Success);

        string first = await Store().StoreAsync(SampleSecret);
        string second = await Store().StoreAsync(SampleSecret);

        first.Should().NotBe(second);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task StoreAsync_NonZeroStatus_ThrowsSecretStoreException()
    {
        _keychain.Add(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(OtherFailure);

        Func<Task> act = () => Store().StoreAsync(SampleSecret);

        await act.Should().ThrowAsync<SecretStoreException>();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task RetrieveAsync_Found_ReturnsSecret()
    {
        _keychain.CopyMatching(Service, "ref-1").Returns((Success, SampleSecret));

        string? retrieved = await Store().RetrieveAsync("ref-1");

        retrieved.Should().Be(SampleSecret);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task RetrieveAsync_NotFound_ReturnsNull()
    {
        _keychain.CopyMatching(Service, "ref-missing").Returns((ItemNotFound, (string?)null));

        string? retrieved = await Store().RetrieveAsync("ref-missing");

        retrieved.Should().BeNull();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task RetrieveAsync_OtherFailure_ThrowsSecretStoreException()
    {
        _keychain.CopyMatching(Service, "ref-1").Returns((OtherFailure, (string?)null));

        Func<Task> act = () => Store().RetrieveAsync("ref-1");

        await act.Should().ThrowAsync<SecretStoreException>();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task DeleteAsync_Found_ReturnsTrue()
    {
        _keychain.Delete(Service, "ref-1").Returns(Success);

        (await Store().DeleteAsync("ref-1")).Should().BeTrue();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task DeleteAsync_NotFound_ReturnsFalse()
    {
        _keychain.Delete(Service, "ref-missing").Returns(ItemNotFound);

        (await Store().DeleteAsync("ref-missing")).Should().BeFalse();
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task DeleteAsync_OtherFailure_ThrowsSecretStoreException()
    {
        _keychain.Delete(Service, "ref-1").Returns(OtherFailure);

        Func<Task> act = () => Store().DeleteAsync("ref-1");

        await act.Should().ThrowAsync<SecretStoreException>();
    }
}
