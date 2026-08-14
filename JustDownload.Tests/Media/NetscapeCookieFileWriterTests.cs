using FluentAssertions;
using JustDownload.Core.Media.YtDlp;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Tests for the Netscape cookie-file serialization the "Sign in to YouTube" feature feeds into the
/// existing <c>--cookies &lt;path&gt;</c> path (<see cref="YtDlpMediaExtractor"/>).
/// </summary>
public sealed class NetscapeCookieFileWriterTests
{
    [Fact]
    public void Write_EmptyList_ProducesOnlyTheHeader()
    {
        string result = NetscapeCookieFileWriter.Write([]);

        result.Should().StartWith("# Netscape HTTP Cookie File");
        result.Should().NotContain("\t");
    }

    [Fact]
    public void Write_OneCookie_ProducesSevenTabSeparatedFields_InOrder()
    {
        var cookie = new NetscapeCookieRecord(
            Domain: ".youtube.com",
            IncludeSubdomains: true,
            Path: "/",
            Secure: true,
            ExpiresUnixSeconds: 1735689600,
            Name: "SID",
            Value: "abc123");

        string result = NetscapeCookieFileWriter.Write([cookie]);

        result.Should().Contain(".youtube.com\tTRUE\t/\tTRUE\t1735689600\tSID\tabc123\n");
    }

    [Fact]
    public void Write_SessionCookie_WritesZeroExpiry()
    {
        var cookie = new NetscapeCookieRecord(
            Domain: "accounts.google.com",
            IncludeSubdomains: false,
            Path: "/",
            Secure: false,
            ExpiresUnixSeconds: 0,
            Name: "session",
            Value: "temp");

        string result = NetscapeCookieFileWriter.Write([cookie]);

        result.Should().Contain("accounts.google.com\tFALSE\t/\tFALSE\t0\tsession\ttemp\n");
    }

    [Fact]
    public void Write_MultipleCookies_WritesOneLinePerCookie()
    {
        NetscapeCookieRecord[] cookies =
        [
            new(".youtube.com", true, "/", true, 100, "A", "1"),
            new(".google.com", true, "/", true, 200, "B", "2"),
        ];

        string result = NetscapeCookieFileWriter.Write(cookies);

        result.Should().Contain("A\t1\n");
        result.Should().Contain("B\t2\n");
    }
}
