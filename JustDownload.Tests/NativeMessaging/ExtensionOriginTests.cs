using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>Tests for validating the calling extension against the allowlist (TASK-064 AC1).</summary>
public sealed class ExtensionOriginTests
{
    private static readonly string[] Allowed = ["abcdefghijklmnopabcdefghijklmnop", "justdownload@justdownload.app"];

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", true)]
    [InlineData("justdownload@justdownload.app", true)]
    [InlineData("chrome-extension://someotherextensionidthatisnotours/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowed_MatchesConfiguredIds(string? origin, bool expected)
    {
        ExtensionOrigin.IsAllowed(origin, Allowed).Should().Be(expected);
    }

    [Fact]
    public void IsAllowed_EmptyAllowlist_RejectsEverything()
    {
        ExtensionOrigin.IsAllowed("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", []).Should().BeFalse();
    }

    [Fact]
    public void FromArguments_PicksChromeOrigin()
    {
        ExtensionOrigin.FromArguments(["chrome-extension://abc/", "ignored"])
            .Should().Be("chrome-extension://abc/");
    }

    [Fact]
    public void FromArguments_PicksFirefoxId()
    {
        ExtensionOrigin.FromArguments(["justdownload@justdownload.app"])
            .Should().Be("justdownload@justdownload.app");
    }

    [Fact]
    public void FromArguments_Empty_ReturnsNull()
    {
        ExtensionOrigin.FromArguments([]).Should().BeNull();
    }

    [Fact]
    public void Categorize_ChromiumOrigin_ReturnsChromium()
    {
        // Real id (TASK-175): Chrome and Edge share it, so a Chromium-origin launch is categorized the
        // same regardless of which of the two actually launched the host.
        string origin = $"chrome-extension://{NativeHostIdentity.ChromiumExtensionId}/";

        ExtensionOrigin.Categorize(origin).Should().Be(ExtensionContactOrigin.Chromium);
    }

    [Fact]
    public void Categorize_FirefoxOrigin_ReturnsFirefox()
    {
        ExtensionOrigin.Categorize(NativeHostIdentity.FirefoxExtensionId).Should().Be(ExtensionContactOrigin.Firefox);
    }

    [Fact]
    public void Categorize_UnrecognizedOrigin_ReturnsNull()
    {
        ExtensionOrigin.Categorize("chrome-extension://someotherextensionidthatisnotours/").Should().BeNull();
    }
}
