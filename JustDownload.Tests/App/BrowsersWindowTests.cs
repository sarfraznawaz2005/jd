using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.NativeMessaging;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless test that the Browsers panel mounts and lists each browser family's connection status
/// (TASK-093 AC0, TASK-175).</summary>
public sealed class BrowsersWindowTests
{
    [AvaloniaFact]
    public void Window_Mounts_AndListsPerBrowserFamilyStatus()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        var tracker = Substitute.For<IExtensionContactTracker>();
        tracker.GetLastContact(ExtensionContactOrigin.Chromium).Returns(DateTimeOffset.UtcNow);
        tracker.GetLastContact(ExtensionContactOrigin.Firefox).Returns((DateTimeOffset?)null);

        var window = new BrowsersWindow
        {
            DataContext = new BrowsersViewModel(installer, tracker, Substitute.For<JustDownload.Core.IPortableEnvironment>()),
        };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.Should().Contain(t => t != null && t.Contains("Chromium", StringComparison.Ordinal));
        texts.Should().Contain("Firefox");
        texts.Should().Contain("Connected");
        texts.Should().Contain("Not connected");
    }
}
