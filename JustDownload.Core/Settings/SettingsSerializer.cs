using System.Globalization;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Settings;

/// <summary>
/// Maps the strongly-typed <see cref="AppSettings"/> record to and from the flat
/// <c>key &#8594; string</c> rows of the persistent settings table, in one place so the key names
/// and (de)serialization stay DRY. Parsing is defensive: an absent or corrupt stored value falls
/// back to the typed default and is logged rather than throwing, keeping startup robust.
/// </summary>
internal static partial class SettingsSerializer
{
    internal const string MaxConcurrentDownloadsKey = "downloads.max_concurrent";
    internal const string ConnectionsPerDownloadKey = "downloads.connections_per_download";
    internal const string GlobalSpeedLimitKey = "downloads.global_speed_limit";
    internal const string DefaultVideoQualityKey = "media.default_video_quality";
    internal const string DefaultContainerKey = "media.default_container";
    internal const string DensityKey = "ui.density";
    internal const string ThemeKey = "ui.theme";

    /// <summary>
    /// Serializes every setting to its storage representation. The result always contains all keys so
    /// callers can diff two snapshots key-by-key. Values use the invariant culture for numbers and the
    /// stable enum member name for enums.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToStorage(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MaxConcurrentDownloadsKey] =
                settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            [ConnectionsPerDownloadKey] =
                settings.ConnectionsPerDownload.ToString(CultureInfo.InvariantCulture),
            [GlobalSpeedLimitKey] =
                settings.GlobalSpeedLimitBytesPerSecond.ToString(CultureInfo.InvariantCulture),
            [DefaultVideoQualityKey] = settings.DefaultVideoQuality.ToString(),
            [DefaultContainerKey] = settings.DefaultContainer.ToString(),
            [DensityKey] = settings.Density.ToString(),
            [ThemeKey] = settings.Theme.ToString(),
        };
    }

    /// <summary>
    /// Builds an <see cref="AppSettings"/> from stored rows, starting from the typed defaults and
    /// overriding each field only when its key is present and parses cleanly. Unknown extra keys are
    /// ignored; unparseable values are logged and left at their default.
    /// </summary>
    public static AppSettings FromStorage(
        IReadOnlyDictionary<string, string?> stored,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(logger);

        var defaults = new AppSettings();

        return defaults with
        {
            MaxConcurrentDownloads =
                ParseInt(stored, MaxConcurrentDownloadsKey, defaults.MaxConcurrentDownloads, logger),
            ConnectionsPerDownload =
                ParseInt(stored, ConnectionsPerDownloadKey, defaults.ConnectionsPerDownload, logger),
            GlobalSpeedLimitBytesPerSecond =
                ParseLong(stored, GlobalSpeedLimitKey, defaults.GlobalSpeedLimitBytesPerSecond, logger),
            DefaultVideoQuality =
                ParseEnum(stored, DefaultVideoQualityKey, defaults.DefaultVideoQuality, logger),
            DefaultContainer =
                ParseEnum(stored, DefaultContainerKey, defaults.DefaultContainer, logger),
            Density = ParseEnum(stored, DensityKey, defaults.Density, logger),
            Theme = ParseEnum(stored, ThemeKey, defaults.Theme, logger),
        };
    }

    private static int ParseInt(
        IReadOnlyDictionary<string, string?> stored,
        string key,
        int fallback,
        ILogger logger)
    {
        if (!stored.TryGetValue(key, out string? raw) || raw is null)
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        LogIgnored(logger, key, raw);
        return fallback;
    }

    private static long ParseLong(
        IReadOnlyDictionary<string, string?> stored,
        string key,
        long fallback,
        ILogger logger)
    {
        if (!stored.TryGetValue(key, out string? raw) || raw is null)
        {
            return fallback;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        LogIgnored(logger, key, raw);
        return fallback;
    }

    private static TEnum ParseEnum<TEnum>(
        IReadOnlyDictionary<string, string?> stored,
        string key,
        TEnum fallback,
        ILogger logger)
        where TEnum : struct, Enum
    {
        if (!stored.TryGetValue(key, out string? raw) || raw is null)
        {
            return fallback;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out TEnum value) && Enum.IsDefined(value))
        {
            return value;
        }

        LogIgnored(logger, key, raw);
        return fallback;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Ignoring unparseable setting {SettingKey}={SettingValue}; using default.")]
    private static partial void LogIgnored(ILogger logger, string settingKey, string settingValue);
}
