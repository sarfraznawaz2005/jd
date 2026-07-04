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
    private static DownloadFolderProvider Build(
        string? configured, bool organizeByCategory = false, string? organizedRoot = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings
        {
            DefaultDownloadDirectory = configured,
            OrganizeByCategory = organizeByCategory,
            OrganizedRootDirectory = organizedRoot,
        });
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
    public void GetFolderForCategory_ComposesUnderTheConfiguredBase_WhenOrganizeByCategoryIsOn()
    {
        DownloadFolderProvider provider = Build(@"X:\MyDownloads", organizeByCategory: true);

        string expected = System.IO.Path.Combine(@"X:\MyDownloads", CategoryFolderRules.CreateDefault().GetFolderName(FileCategory.Program));
        provider.GetFolderForCategory(FileCategory.Program).Should().Be(expected);
    }

    [Fact]
    public void GetFolderForCategory_ComposesUnderTheOrganizedRoot_WhenSet()
    {
        DownloadFolderProvider provider = Build(@"X:\MyDownloads", organizeByCategory: true, organizedRoot: @"Y:\Sorted");

        string expected = System.IO.Path.Combine(@"Y:\Sorted", CategoryFolderRules.CreateDefault().GetFolderName(FileCategory.Program));
        provider.GetFolderForCategory(FileCategory.Program).Should().Be(expected);
    }

    [Fact]
    public void GetFolderForCategory_ReturnsBaseFolder_WhenOrganizeByCategoryIsOff()
    {
        // The New Download dialog must not suggest a category subfolder the file will never actually be
        // organized into (user-reported: it showed a "Programs" folder that didn't exist, and never would,
        // with the toggle off).
        DownloadFolderProvider provider = Build(@"X:\MyDownloads", organizeByCategory: false);

        provider.GetFolderForCategory(FileCategory.Program).Should().Be(@"X:\MyDownloads");
    }
}
