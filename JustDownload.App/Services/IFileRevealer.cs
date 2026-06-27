namespace JustDownload.App.Services;

/// <summary>
/// Opens a downloaded file or reveals it in the OS file manager — the "open" / "open containing folder"
/// context-menu actions (TASK-051). Implementations shell out to the platform file manager.
/// </summary>
public interface IFileRevealer
{
    /// <summary>Opens the file with its default application. No-op if the path is missing.</summary>
    void OpenFile(string? path);

    /// <summary>Reveals the file in the OS file manager (selecting it where supported).</summary>
    void RevealInFolder(string? path);
}
