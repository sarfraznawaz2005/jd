using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless smoke test that the New URL dialog window mounts and binds its view-model (TASK-053).</summary>
public sealed class NewDownloadWindowTests
{
    private static NewDownloadViewModel BuildViewModel()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        var folders = Substitute.For<IDownloadFolderProvider>();
        folders.GetBaseFolder().Returns(@"C:\Downloads");
        folders.GetFolderForCategory(Arg.Any<FileCategory>()).Returns(@"C:\Downloads\Programs");
        return new NewDownloadViewModel(
            Substitute.For<IResourceProbe>(),
            Substitute.For<IFileCategorizer>(),
            folders,
            settings,
            Substitute.For<IDownloadManager>(),
            Substitute.For<IDownloadActions>(),
            Substitute.For<IDuplicateDownloadCheck>(),
            NullLogger<NewDownloadViewModel>.Instance);
    }

    [AvaloniaFact]
    public void Window_Mounts_WithUrlBoxAndActions()
    {
        var window = new NewDownloadWindow { DataContext = BuildViewModel() };
        window.Show();

        window.FindControl<TextBox>("UrlBox").Should().NotBeNull("the URL field is the primary input");
        window.FindControl<Button>("BrowseButton").Should().NotBeNull("the folder picker is reachable");
    }
}
