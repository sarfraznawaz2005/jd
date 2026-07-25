using Avalonia.Controls;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The shared "are you sure" dialog: a thin shell over <see cref="ConfirmViewModel"/>. The view-model owns
/// the copy and the two actions; this code-behind only closes the window with the chosen answer so the
/// caller's <c>ShowDialog&lt;bool&gt;</c> resolves with it.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ConfirmViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool confirmed) => Close(confirmed);
}
