namespace JustDownload.App.Services;

/// <summary>Copies text to the system clipboard — the "copy link" context-menu action (TASK-051).</summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard. A null/empty value is ignored.</summary>
    Task CopyAsync(string? text);
}
