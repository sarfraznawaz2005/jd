using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Settings;

/// <summary>
/// Exports the current preferences to a portable JSON file and imports them back, so settings can be
/// backed up or migrated to another machine (TASK-129). The on-disk shape is a small versioned envelope
/// around the same flat key&#8594;string rows the engine already persists, reusing
/// <see cref="SettingsSerializer"/> so a round-trip restores every value and forward-incompatible keys
/// degrade gracefully (unknown keys ignored, unparseable ones defaulted).
/// </summary>
public interface ISettingsTransfer
{
    /// <summary>Writes <paramref name="settings"/> to <paramref name="filePath"/> as a JSON export.</summary>
    Task ExportAsync(AppSettings settings, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a JSON export written by <see cref="ExportAsync"/> and returns the corresponding settings.
    /// Throws <see cref="InvalidDataException"/> when the file is not a recognizable export.
    /// </summary>
    Task<AppSettings> ImportAsync(string filePath, CancellationToken cancellationToken = default);
}

internal sealed partial class SettingsTransfer : ISettingsTransfer
{
    private const int CurrentSchema = 1;

    private readonly ILogger<SettingsTransfer> _logger;

    public SettingsTransfer(ILogger<SettingsTransfer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task ExportAsync(AppSettings settings, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // The proxy password is a machine-local keychain handle (an opaque ref, not the secret). It can't
        // resolve on another machine and exporting it would only mislead — leave it out (CLAUDE.md §5).
        Dictionary<string, string> values = SettingsSerializer.ToStorage(settings)
            .Where(kv => kv.Key != SettingsSerializer.ProxyPasswordSecretRefKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var envelope = new SettingsExportFile { Schema = CurrentSchema, Values = values };
        string json = JsonSerializer.Serialize(
            envelope, SettingsTransferJsonContext.Default.SettingsExportFile);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSettings> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

        SettingsExportFile? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                json, SettingsTransferJsonContext.Default.SettingsExportFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The selected file is not a valid JustDownload settings export.", ex);
        }

        if (envelope?.Values is null)
        {
            throw new InvalidDataException("The selected file is not a valid JustDownload settings export.");
        }

        if (envelope.Schema != CurrentSchema)
        {
            // Unknown schema: still attempt a best-effort import (FromStorage ignores unknown keys and
            // defaults the rest) rather than refusing, but record it.
            LogUnexpectedSchema(_logger, envelope.Schema, CurrentSchema);
        }

        Dictionary<string, string?> rows =
            envelope.Values.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal);
        return SettingsSerializer.FromStorage(rows, _logger);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Importing a settings file with schema {FoundSchema}; this build expects {ExpectedSchema}. Doing a best-effort import.")]
    private static partial void LogUnexpectedSchema(ILogger logger, int foundSchema, int expectedSchema);
}

/// <summary>The on-disk envelope for a settings export: a schema version plus the flat setting rows.</summary>
internal sealed record SettingsExportFile
{
    [JsonPropertyName("schema")]
    public required int Schema { get; init; }

    [JsonPropertyName("values")]
    public required Dictionary<string, string> Values { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsExportFile))]
internal sealed partial class SettingsTransferJsonContext : JsonSerializerContext;
