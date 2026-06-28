using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The command palette (TASK-056, PRD §2.4.2): a searchable list of the app's core commands — New URL, jump
/// to a category, toggle theme/density, change limits — opened with Ctrl/Cmd+K. As the user types, the
/// results filter live; Enter runs the selected command and closes the palette. Pure view-model logic so it
/// is fully unit-testable; the shell owns the keybinding and the overlay view.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PaletteCommand> _commands;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteCommand? _selected;

    public CommandPaletteViewModel(IReadOnlyList<PaletteCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        RefreshResults();
    }

    /// <summary>The commands matching the current <see cref="Query"/>, in declared order.</summary>
    public ObservableCollection<PaletteCommand> Results { get; } = new();

    /// <summary>Opens the palette with a cleared query and the first result selected.</summary>
    public void Open()
    {
        Query = string.Empty;
        RefreshResults();
        IsOpen = true;
    }

    /// <summary>Closes the palette.</summary>
    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Runs <paramref name="command"/> (or the current selection) and closes the palette.</summary>
    [RelayCommand]
    public void Execute(PaletteCommand? command)
    {
        PaletteCommand? target = command ?? Selected;
        if (target is null)
        {
            return;
        }

        // Close first so a command that opens a dialog isn't layered under the palette overlay.
        IsOpen = false;
        target.Run();
    }

    /// <summary>Moves the selection by <paramref name="delta"/> within the current results (arrow keys).</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        int index = Selected is null ? -1 : Results.IndexOf(Selected);
        index = Math.Clamp(index + delta, 0, Results.Count - 1);
        Selected = Results[index];
    }

    partial void OnQueryChanged(string value) => RefreshResults();

    private void RefreshResults()
    {
        Results.Clear();
        foreach (PaletteCommand command in _commands)
        {
            if (command.Matches(Query))
            {
                Results.Add(command);
            }
        }

        Selected = Results.Count > 0 ? Results[0] : null;
    }
}
