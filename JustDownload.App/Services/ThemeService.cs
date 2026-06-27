using Avalonia;
using Avalonia.Styling;

namespace JustDownload.App.Services;

/// <summary>The user-selectable theme (TASK-047, US-15 AC2). <see cref="System"/> follows the OS.</summary>
public enum ThemeMode
{
    /// <summary>The product default — always light.</summary>
    Light,

    /// <summary>Dark theme.</summary>
    Dark,

    /// <summary>Follow the operating-system theme.</summary>
    System,
}

/// <summary>Pure mapping/cycling helpers for <see cref="ThemeMode"/>, unit-testable without an app instance.</summary>
public static class ThemeCycle
{
    /// <summary>The next mode in the Light → Dark → System cycle.</summary>
    public static ThemeMode Next(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ThemeMode.Dark,
        ThemeMode.Dark => ThemeMode.System,
        ThemeMode.System => ThemeMode.Light,
        _ => ThemeMode.Light,
    };

    /// <summary>The Avalonia <see cref="ThemeVariant"/> a mode maps to (<see cref="ThemeMode.System"/> = follow OS).</summary>
    public static ThemeVariant ToVariant(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ThemeVariant.Light,
        ThemeMode.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}

/// <summary>Applies and cycles the application theme (TASK-047). Light by default; can follow the OS.</summary>
public interface IThemeService
{
    /// <summary>The current mode.</summary>
    ThemeMode Mode { get; }

    /// <summary>Raised after the mode changes.</summary>
    event EventHandler? Changed;

    /// <summary>Sets the mode and applies it to the running application.</summary>
    void SetMode(ThemeMode mode);

    /// <summary>Advances to the next mode (Light → Dark → System → Light).</summary>
    void Toggle();
}

/// <summary>
/// Default <see cref="IThemeService"/>. Holds the selected <see cref="ThemeMode"/> and pushes the matching
/// <see cref="ThemeVariant"/> onto <see cref="Application.Current"/>. Starts at <see cref="ThemeMode.Light"/>
/// to match the App's default variant.
/// </summary>
public sealed class ThemeService : IThemeService
{
    public ThemeMode Mode { get; private set; } = ThemeMode.Light;

    public event EventHandler? Changed;

    public void SetMode(ThemeMode mode)
    {
        Mode = mode;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeCycle.ToVariant(mode);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle() => SetMode(ThemeCycle.Next(Mode));
}
