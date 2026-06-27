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
}
