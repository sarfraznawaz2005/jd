using FluentAssertions;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// The bridge that applies the persisted proxy settings to the engine's proxy service (TASK-125): it builds
/// the right <see cref="ProxyConfiguration"/> from settings, resolves the auth password from the keychain,
/// and re-applies on a settings change — so a saved proxy is actually routed through.
/// </summary>
public sealed class GlobalProxyControllerTests
{
    private static ISettingsService SettingsWith(AppSettings current)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(current);
        return settings;
    }

    [Fact]
    public async Task ApplyCurrentAsync_WithProxyAndPassword_SetsGlobalProxyWithResolvedCredentials()
    {
        var current = new AppSettings
        {
            ProxyKind = ProxyKind.Http,
            ProxyHost = "proxy.local",
            ProxyPort = 8080,
            ProxyUsername = "user",
            ProxyDomain = "CORP",
            ProxyPasswordSecretRef = "ref-1",
        };
        var proxy = Substitute.For<IProxyService>();
        var secrets = Substitute.For<ISecretStore>();
        secrets.RetrieveAsync("ref-1", Arg.Any<CancellationToken>()).Returns("s3cret");

        using var controller = new GlobalProxyController(
            SettingsWith(current), proxy, secrets, NullLogger<GlobalProxyController>.Instance);
        await controller.ApplyCurrentAsync();

        proxy.Received(1).SetGlobalProxy(Arg.Is<ProxyConfiguration>(c =>
            c.Kind == ProxyKind.Http && c.Host == "proxy.local" && c.Port == 8080 &&
            c.Credentials != null && c.Credentials.Username == "user" &&
            c.Credentials.Password == "s3cret" && c.Credentials.Domain == "CORP"));
    }

    [Fact]
    public async Task ApplyCurrentAsync_WithNoHost_SetsDirectConnection()
    {
        var proxy = Substitute.For<IProxyService>();
        using var controller = new GlobalProxyController(
            SettingsWith(new AppSettings { ProxyKind = ProxyKind.None }),
            proxy, Substitute.For<ISecretStore>(), NullLogger<GlobalProxyController>.Instance);

        await controller.ApplyCurrentAsync();

        proxy.Received(1).SetGlobalProxy(Arg.Is<ProxyConfiguration>(c => c.Kind == ProxyKind.None));
    }

    [Fact]
    public async Task ApplyCurrentAsync_WithNoUsername_SetsProxyWithoutCredentials()
    {
        var current = new AppSettings { ProxyKind = ProxyKind.Socks5, ProxyHost = "s5.local", ProxyPort = 1080 };
        var proxy = Substitute.For<IProxyService>();
        var secrets = Substitute.For<ISecretStore>();

        using var controller = new GlobalProxyController(
            SettingsWith(current), proxy, secrets, NullLogger<GlobalProxyController>.Instance);
        await controller.ApplyCurrentAsync();

        proxy.Received(1).SetGlobalProxy(Arg.Is<ProxyConfiguration>(c =>
            c.Kind == ProxyKind.Socks5 && c.Host == "s5.local" && c.Credentials == null));
        await secrets.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SettingsChanged_ReAppliesProxy()
    {
        var proxy = Substitute.For<IProxyService>();
        var settings = SettingsWith(new AppSettings());
        using var controller = new GlobalProxyController(
            settings, proxy, Substitute.For<ISecretStore>(), NullLogger<GlobalProxyController>.Instance);

        // A change to a no-auth proxy applies synchronously (no keychain await), so it is observable here.
        var updated = new AppSettings { ProxyKind = ProxyKind.Socks5, ProxyHost = "s5.local", ProxyPort = 1080 };
        settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settings, new SettingsChangedEventArgs(new AppSettings(), updated, Array.Empty<string>()));

        proxy.Received(1).SetGlobalProxy(Arg.Is<ProxyConfiguration>(c =>
            c.Kind == ProxyKind.Socks5 && c.Host == "s5.local"));
    }
}
