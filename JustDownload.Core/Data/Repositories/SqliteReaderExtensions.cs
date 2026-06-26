using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Small null-aware read helpers shared by the repositories so each mapper stays DRY and reads
/// nullable columns the same way. <see cref="DateTimeOffset"/> values are stored as ISO-8601
/// round-trip strings (UTC) and parsed back invariantly so persistence is culture-independent.
/// </summary>
internal static class SqliteReaderExtensions
{
    /// <summary>The format used to persist timestamps: ISO-8601 round-trip ("O").</summary>
    public const string TimestampFormat = "O";

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetNullableInt32(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static DateTimeOffset GetDateTimeOffset(this SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(
            reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? GetNullableDateTimeOffset(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTimeOffset(ordinal);

    /// <summary>Formats a timestamp for storage as an invariant ISO-8601 round-trip string.</summary>
    public static string ToStorage(this DateTimeOffset value)
        => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
