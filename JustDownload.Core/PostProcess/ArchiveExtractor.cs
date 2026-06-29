using System.IO.Compression;

namespace JustDownload.Core.PostProcess;

/// <summary>
/// Extracts a completed archive download into a sibling folder (TASK-135). Recognises the format by file
/// extension. Today only <c>.zip</c> is handled via the BCL's <see cref="ZipFile"/> (no third-party
/// dependency); <c>.7z</c>/<c>.rar</c> need a permissive library and are a follow-up.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>Whether <paramref name="filePath"/> is an archive this extractor can unpack.</summary>
    bool CanExtract(string filePath);

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into a new folder beside it named after the archive, and
    /// returns that folder. Throws if the file is not a supported archive or the destination already exists.
    /// </summary>
    Task<string> ExtractAsync(string archivePath, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IArchiveExtractor"/> — <c>.zip</c> via <see cref="ZipFile"/>.</summary>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    public bool CanExtract(string filePath) =>
        !string.IsNullOrEmpty(filePath) &&
        string.Equals(Path.GetExtension(filePath), ".zip", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (!CanExtract(archivePath))
        {
            throw new NotSupportedException($"'{Path.GetExtension(archivePath)}' archives are not supported.");
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? ".";
        string destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(archivePath));

        // ZipFile.ExtractToDirectory (no overwrite) is hardened against path-traversal ("zip slip"): an entry
        // resolving outside the destination throws rather than escaping.
        await Task.Run(
            () => ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false),
            cancellationToken).ConfigureAwait(false);

        return destination;
    }
}
