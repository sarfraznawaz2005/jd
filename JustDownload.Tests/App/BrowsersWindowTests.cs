using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.NativeMessaging.Registration;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless test that the Browsers panel mounts and lists each browser's status (TASK-093 AC0).</summary>
public sealed class BrowsersWindowTests
{
    [AvaloniaFact]
    public void Window_Mounts_AndListsPerBrowserStatus()
    {
        var installer = Substitute.For<INativeHostInstaller>();
        installer.IsHostPresent.Returns(true);
        installer.GetStatus().Returns(new[]
        {
            new BrowserRegistrationStatus(NativeMessagingBrowser.Chrome, true),
            new BrowserRegistrationStatus(NativeMessagingBrowser.Edge, false),
            new BrowserRegistrationStatus(NativeMessagingBrowser.Firefox, false),
        });

        var window = new BrowsersWindow { DataContext = new BrowsersViewModel(installer) };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.Should().Contain("Chrome");
        texts.Should().Contain("Firefox");
        texts.Should().Contain("Connected");
    }
}
