namespace JustDownload.Core.Settings;

/// <summary>
/// The application's visual theme (CLAUDE.md §7, locked decision D4). <see cref="Light"/> is the
/// product default; <see cref="Dark"/> is optional and the host may additionally choose to follow
/// the OS theme on top of this stored preference.
/// </summary>
public enum AppTheme
{
    /// <summary>The default light theme.</summary>
    Light = 0,

    /// <summary>The optional dark theme.</summary>
    Dark = 1,
}
