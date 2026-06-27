namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// A settings section whose backing subsystem is not yet a persisted preference (TASK-057): Proxy,
/// Authentication, Browsers, Advanced. It honestly describes where that capability currently lives rather
/// than presenting controls that would not persist — the editable settings arrive with each subsystem's own
/// task. Holds a heading and one or more explanatory paragraphs.
/// </summary>
public sealed class InfoSettingsViewModel : ViewModelBase
{
    public InfoSettingsViewModel(string heading, params string[] paragraphs)
    {
        Heading = heading;
        Paragraphs = paragraphs;
    }

    /// <summary>The section heading.</summary>
    public string Heading { get; }

    /// <summary>The explanatory paragraphs shown under the heading.</summary>
    public IReadOnlyList<string> Paragraphs { get; }
}
