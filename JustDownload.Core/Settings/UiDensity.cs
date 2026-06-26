namespace JustDownload.Core.Settings;

/// <summary>
/// The list/layout density (PRD §2.4.4 density toggle). <see cref="Comfortable"/> is the default;
/// <see cref="Compact"/> is the power-user, higher-density mode.
/// </summary>
public enum UiDensity
{
    /// <summary>The default, roomier layout.</summary>
    Comfortable = 0,

    /// <summary>The denser, power-user layout.</summary>
    Compact = 1,
}
