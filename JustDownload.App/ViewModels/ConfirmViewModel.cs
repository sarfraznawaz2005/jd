using CommunityToolkit.Mvvm.Input;

namespace JustDownload.App.ViewModels;

/// <summary>
/// A yes/no confirmation prompt for a destructive action. The caller supplies the copy so this stays the one
/// dialog for every "are you sure" in the app rather than a new window per action; the view binds these
/// properties directly and has no dependencies, keeping it headless-testable.
/// </summary>
public sealed partial class ConfirmViewModel : ViewModelBase
{
    public ConfirmViewModel(string heading, string message, string confirmLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        Heading = heading;
        Message = message;
        ConfirmLabel = confirmLabel;
    }

    public string Heading { get; }

    public string Message { get; }

    /// <summary>The affirmative button's text — name the action ("Re-download"), never just "OK".</summary>
    public string ConfirmLabel { get; }

    /// <summary>Raised with the user's answer once they pick an action.</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
