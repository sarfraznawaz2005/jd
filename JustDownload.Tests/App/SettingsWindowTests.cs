using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels.Settings;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless test that the settings window mounts with its section nav and content (TASK-057).</summary>
public sealed class SettingsWindowTests
{
    [AvaloniaFact]
    public void Window_Mounts_AndShowsSelectedSectionContent()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        var vm = new SettingsViewModel(
            settings, new ThemeService(), CategoryFolderRules.CreateDefault(),
            Substitute.For<INativeHostInstaller>(), Substitute.For<ISecretStore>(),
            Substitute.For<ISettingsTransfer>(), Substitute.For<IProxyTester>(),
            Substitute.For<JustDownload.Core.IPortableEnvironment>(), Substitute.For<JustDownload.Core.Security.ISavedCredentialsService>());
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        // The nav lists all seven sections, and the content area renders the General section's controls.
        window.GetVisualDescendants().OfType<ComboBox>().Should().NotBeEmpty("the General section binds combo boxes");
        vm.Sections.Should().HaveCount(7);
    }
}
