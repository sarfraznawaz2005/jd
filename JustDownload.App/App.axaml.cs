using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core;
using JustDownload.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JustDownload.App;

/// <summary>The Avalonia application root; wires up the main window on startup.</summary>
public partial class App : Application
{
    /// <summary>
    /// The application's service provider: Core's single composition root (§6) plus the App-layer services
    /// (theme, view-models). ViewModels resolve their Core/App dependencies from here.
    /// </summary>
    public IServiceProvider Services { get; } =
        new ServiceCollection()
            .AddJustDownloadCore()
            .AddSingleton<IThemeService, ThemeService>()
            .AddSingleton<MainWindowViewModel>()
            .BuildServiceProvider();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Capture and surface unhandled exceptions process-wide — no silent failures (§1).
        Services.GetRequiredService<IGlobalErrorHandler>().Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
