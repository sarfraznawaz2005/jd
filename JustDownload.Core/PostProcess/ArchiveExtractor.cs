using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace JustDownload.Core.PostProcess;

/// <summary>
/// Extracts a completed archive download into a sibling folder (TASK-135). Recognises the format by file
/// extension: <c>.zip</c> via the BCL's <see cref="ZipFile"/>; <c>.7z</c>/<c>.rar</c> via SharpCompress
/// (TASK-156) — SharpCompress can only read those two formats (no encoder exists for either), which matches
/// this app's needs, since it never authors archives.
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

/// <summary>
/// Default <see cref="IArchiveExtractor"/> — <c>.zip</c> via <see cref="ZipFile"/>, <c>.7z</c>/<c>.rar</c> via
/// SharpCompress.
/// </summary>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    private static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar"];

    public bool CanExtract(string filePath) =>
        !string.IsNullOrEmpty(filePath) &&
        SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (!CanExtract(archivePath))
        {
            throw new NotSupportedException($"'{Path.GetExtension(archivePath)}' archives are not supported.");
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? ".";
        string destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(archivePath));

        if (string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ZipFile.ExtractToDirectory (no overwrite) is hardened against path-traversal ("zip slip"): an
            // entry resolving outside the destination throws rather than escaping.
            await Task.Run(
                () => ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => ExtractWithSharpCompress(archivePath, destination), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination;
    }

    // .7z/.rar via SharpCompress. Entries are written one at a time via the low-level per-entry WriteToFile
    // API (which takes the destination path verbatim, with no path derivation of its own) rather than the
    // library's higher-level directory-extraction helpers, so JustDownload's own ResolveEntryPath guard below
    // is the sole authority on where a byte lands — giving the same path-traversal ("zip slip") guarantee as
    // the .zip path above, without depending on unverified library-internal behavior.
    private static void ExtractWithSharpCompress(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        using IArchive archive = ArchiveFactory.Open(archivePath);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.Key is null)
            {
                continue;
            }

            string target = ResolveEntryPath(root, entry.Key);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? root);
            entry.WriteToFile(target, new ExtractionOptions { Overwrite = false });
        }
    }

    /// <summary>
    /// Resolves an archive entry's key to an absolute path under <paramref name="rootWithTrailingSeparator"/>,
    /// throwing if the entry would escape it (a malicious "../" entry — "zip slip"). Internal and pure so it
    /// is directly unit-testable without needing a crafted malicious .7z/.rar archive.
    /// </summary>
    internal static string ResolveEntryPath(string rootWithTrailingSeparator, string entryKey)
    {
        string normalizedKey = entryKey
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(rootWithTrailingSeparator, normalizedKey));

        // Windows/macOS paths are case-insensitive; Linux is case-sensitive.
        StringComparison comparison =
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!target.StartsWith(rootWithTrailingSeparator, comparison))
        {
            throw new IOException($"Archive entry '{entryKey}' would extract outside the destination directory.");
        }

        return target;
    }
}
