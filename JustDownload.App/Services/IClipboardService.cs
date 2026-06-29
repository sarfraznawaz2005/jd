namespace JustDownload.App.Services;

/// <summary>Reads/writes the system clipboard — the "copy link" action (TASK-051) and the clipboard watcher (TASK-133).</summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard. A null/empty value is ignored.</summary>
    Task CopyAsync(string? text);

    /// <summary>Reads the clipboard's text, or <see langword="null"/> if it holds none (TASK-133).</summary>
    Task<string?> GetTextAsync();

    /// <summary>
    /// The text this app last placed on the clipboard via <see cref="CopyAsync"/>, so the clipboard watcher
    /// can ignore the app's own copies and not offer to re-download a link the user just copied (TASK-133).
    /// </summary>
    string? LastCopiedText { get; }
}
