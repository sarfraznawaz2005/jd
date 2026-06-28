using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace JustDownload.App.Services;

/// <summary>
/// Builds the system-tray menu (TASK-061 AC1): Show JustDownload, New download, and Quit. Kept as a small
/// factory so the menu's items and their actions are unit-testable without standing up a real tray icon
/// (which needs a platform tray that headless tests do not provide).
/// </summary>
public static class TrayMenuFactory
{
    /// <summary>Creates the tray menu wired to the given actions.</summary>
    public static NativeMenu Create(Action show, Action newDownload, Action quit)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(newDownload);
        ArgumentNullException.ThrowIfNull(quit);

        var menu = new NativeMenu();
        menu.Items.Add(Item("Show JustDownload", show));
        menu.Items.Add(Item("New download…", newDownload));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit", quit));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action action) =>
        new() { Header = header, Command = new RelayCommand(action) };
}
