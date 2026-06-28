using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// The native-host identity + allowlist (TASK-090): the host accepts the real signed extension and rejects
/// everything else, and the DI-registered <see cref="NativeHostOptions"/> is driven by the single identity
/// source so the runtime allowlist can't drift from the registered manifests.
/// </summary>
public sealed class NativeHostIdentityTests
{
    [Fact]
    public void Allowlist_AcceptsFirefoxId_RejectsUnknownAndBlank()
    {
        IReadOnlyList<string> allow = NativeHostIdentity.AllowedExtensionIds;

        ExtensionOrigin.IsAllowed(NativeHostIdentity.FirefoxExtensionId, allow).Should().BeTrue();
        ExtensionOrigin.IsAllowed("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", allow).Should().BeFalse();
        ExtensionOrigin.IsAllowed("", allow).Should().BeFalse();
        ExtensionOrigin.IsAllowed(null, allow).Should().BeFalse();
    }

    [Fact]
    public void DependencyInjection_PopulatesHostAllowlistFromIdentity()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddJustDownloadNativeMessaging()
            .BuildServiceProvider();

        NativeHostOptions options = provider.GetRequiredService<NativeHostOptions>();

        options.AllowedExtensionIds.Should().Contain(NativeHostIdentity.FirefoxExtensionId,
            "the host must accept the real signed extension out of the box");
        ExtensionOrigin.IsAllowed(NativeHostIdentity.FirefoxExtensionId, options.AllowedExtensionIds).Should().BeTrue();
    }
}
