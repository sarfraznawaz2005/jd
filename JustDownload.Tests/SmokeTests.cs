using FluentAssertions;
using JustDownload.Core;
using Xunit;

namespace JustDownload.Tests;

/// <summary>
/// Scaffold smoke tests: prove the test harness runs and that Tests can reference Core.
/// Real engine tests (segmentation, resume, throttling) arrive with their features.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void AppInfo_Name_IsJustDownload()
    {
        AppInfo.Name.Should().Be("JustDownload");
    }
}
