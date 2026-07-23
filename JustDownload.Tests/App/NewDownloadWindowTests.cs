using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless smoke test that the New URL dialog window mounts and binds its view-model (TASK-053).</summary>
public sealed class NewDownloadWindowTests
{
    private static NewDownloadViewModel BuildViewModel(IResourceProbe? probe = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        var folders = Substitute.For<IDownloadFolderProvider>();
        folders.GetBaseFolder().Returns(@"C:\Downloads");
        folders.GetFolderForCategory(Arg.Any<FileCategory>()).Returns(@"C:\Downloads\Programs");
        return new NewDownloadViewModel(
            probe ?? Substitute.For<IResourceProbe>(),
            Substitute.For<IFileCategorizer>(),
            folders,
            settings,
            Substitute.For<IDownloadManager>(),
            Substitute.For<IDownloadActions>(),
            Substitute.For<IDuplicateDownloadCheck>(),
            Substitute.For<ISecretStore>(),
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

    [AvaloniaFact]
    public async Task TypingIntoUrlBox_TriggersDetection_WithoutLosingFocus()
    {
        // Regression/investigation (user-reported, repeatedly, as still broken): detection must fire ~500ms
        // after the user stops typing, without ever needing LostFocus/Enter.
        var probe = Substitute.For<IResourceProbe>();
        probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ResourceProbeResult
            {
                FinalUri = new Uri("https://example.com/file.zip"),
                StatusCode = 200,
                SupportsRanges = true,
                TotalLength = 1000,
                SuggestedFileName = "file.zip",
            }));
        var window = new NewDownloadWindow { DataContext = BuildViewModel(probe) };
        window.Show();
        TextBox urlBox = window.FindControl<TextBox>("UrlBox")!;
        urlBox.Focus();

        // Simulate typing character-by-character, as UpdateSourceTrigger=PropertyChanged would see it.
        foreach (char c in "https://example.com/file.zip")
        {
            urlBox.Text += c;
            Dispatcher.UIThread.RunJobs();
        }

        urlBox.IsFocused.Should().BeTrue("focus must still be in the URL field — no blur happened");
        await probe.DidNotReceive().ProbeAsync(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>());

        // Let the 500ms debounce elapse without any focus change.
        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        urlBox.IsFocused.Should().BeTrue("still no blur");
        await probe.Received(1).ProbeAsync(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaFact]
    public async Task PrefilledUrl_TriggersDetection_Immediately_WithoutTypingOrBlur()
    {
        // Regression — the actual root cause behind the repeatedly-reported "auto-detect broken": clicking a
        // download link on a website (browser-extension hand-off) or a dropped/forwarded link sets Url on
        // the view-model BEFORE it becomes this window's DataContext, so no Url PropertyChanged ever reaches
        // the debounce wiring — only manual typing (which changes Url AFTER the subscription exists) worked.
        var probe = Substitute.For<IResourceProbe>();
        probe.ProbeAsync(Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ResourceProbeResult
            {
                FinalUri = new Uri("https://example.com/file.zip"),
                StatusCode = 200,
                SupportsRanges = true,
                TotalLength = 1000,
                SuggestedFileName = "file.zip",
            }));
        NewDownloadViewModel vm = BuildViewModel(probe);
        vm.Url = "https://example.com/file.zip"; // set before the window/DataContext exist, like a real hand-off

        var window = new NewDownloadWindow { DataContext = vm };
        window.Show();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        await probe.Received(1).ProbeAsync(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>(), Arg.Any<CancellationToken>());
    }
}
