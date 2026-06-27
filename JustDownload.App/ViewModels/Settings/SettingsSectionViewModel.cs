using CommunityToolkit.Mvvm.ComponentModel;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>One entry in the settings navigation rail (TASK-057): a label, icon, and the section's content view-model.</summary>
public sealed partial class SettingsSectionViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    public SettingsSectionViewModel(string label, string iconKey, ViewModelBase content)
    {
        Label = label;
        IconKey = iconKey;
        Content = content;
    }

    /// <summary>The display label.</summary>
    public string Label { get; }

    /// <summary>The icon-geometry resource key (resolved via the resource-key converter).</summary>
    public string IconKey { get; }

    /// <summary>The view-model rendered when this section is selected.</summary>
    public ViewModelBase Content { get; }
}
