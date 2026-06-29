using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The default-save-location resolver (TASK-121): it honors the configured default download folder and
/// falls back to the OS Downloads folder when none is set, with category subfolders composed under the base.
/// </summary>
public sealed class DownloadFolderProviderTests
{
    private static DownloadFolderProvider Build(string? configured)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { DefaultDownloadDirectory = configured });
        return new DownloadFolderProvider(CategoryFolderRules.CreateDefault(), settings);
    }

    [Fact]
    public void GetBaseFolder_UsesConfiguredDirectory_WhenSet()
    {
        DownloadFolderProvider provider = Build(@"X:\MyDownloads");

        provider.GetBaseFolder().Should().Be(@"X:\MyDownloads");
    }

    [Fact]
    public void GetBaseFolder_TrimsConfiguredDirectory()
    {
        DownloadFolderProvider provider = Build("  X:\\MyDownloads  ");

        provider.GetBaseFolder().Should().Be(@"X:\MyDownloads");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetBaseFolder_FallsBackToOsDownloads_WhenUnset(string? configured)
    {
        DownloadFolderProvider provider = Build(configured);

        provider.GetBaseFolder().Should().EndWith("Downloads");
    }

    [Fact]
    public void GetFolderForCategory_ComposesUnderTheConfiguredBase()
    {
        DownloadFolderProvider provider = Build(@"X:\MyDownloads");

        string expected = System.IO.Path.Combine(@"X:\MyDownloads", CategoryFolderRules.CreateDefault().GetFolderName(FileCategory.Program));
        provider.GetFolderForCategory(FileCategory.Program).Should().Be(expected);
    }
}
