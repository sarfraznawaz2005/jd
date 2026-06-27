using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless UI tests for the app shell (TASK-047): the window mounts and is usable (AC0), light is the
/// default theme and dark/OS are reachable (AC1), the design tokens are centralized and resolve per variant
/// (AC2), and they carry the mockup's accent (AC3).
/// </summary>
public sealed class ShellTests
{
    [AvaloniaFact]
    public void DefaultThemeIsLight()
    {
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void DesignTokens_ResolvePerVariant_AndCarryMockupAccent()
    {
        bool gotLight = Application.Current!.TryGetResource("AccentBrush", ThemeVariant.Light, out object? light);
        bool gotDark = Application.Current!.TryGetResource("AccentBrush", ThemeVariant.Dark, out object? dark);

        gotLight.Should().BeTrue();
        gotDark.Should().BeTrue();

        // The accent matches mockups/styles.css (light #5b67d6) — proving tokens are centralized and applied.
        ((SolidColorBrush)light!).Color.Should().Be(Color.Parse("#5b67d6"));
        ((SolidColorBrush)dark!).Color.Should().Be(Color.Parse("#5e6ad2"));

        // Spacing/radius metrics are present too (theme-independent tokens).
        Application.Current!.TryGetResource("Space4", null, out object? space).Should().BeTrue();
        space.Should().Be(16d);
    }

    [AvaloniaFact]
    public void MainWindow_MountsWithShellChrome()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(new ThemeService()),
        };
        window.Show();

        window.FindControl<Border>("Toolbar").Should().NotBeNull("the toolbar is part of the shell");
        window.FindControl<Border>("Sidebar").Should().NotBeNull("the sidebar is part of the shell");
        window.FindControl<Border>("StatusBar").Should().NotBeNull("the status bar is part of the shell");
        window.FindControl<StackPanel>("EmptyState").Should().NotBeNull("the empty-state placeholder shows");
    }

    [AvaloniaFact]
    public void ThemeToggle_SwitchesVariant_ThenRestores()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        try
        {
            var theme = new ThemeService();
            theme.Mode.Should().Be(ThemeMode.Light);

            theme.Toggle();
            theme.Mode.Should().Be(ThemeMode.Dark);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

            theme.Toggle();
            theme.Mode.Should().Be(ThemeMode.System);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Default);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original; // don't leak theme state to other tests
        }
    }
}
