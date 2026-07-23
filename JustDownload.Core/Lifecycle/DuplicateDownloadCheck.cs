using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;

namespace JustDownload.Core.Lifecycle;

/// <summary>How a prospective download duplicates something that already exists (TASK-139).</summary>
public enum DuplicateKind
{
    /// <summary>No duplicate — the destination is free and not in the library.</summary>
    None = 0,

    /// <summary>A file already exists at the destination path on disk.</summary>
    FileExistsOnDisk = 1,

    /// <summary>A download to the same destination already exists in the library/history and is still
    /// claiming it (queued, downloading, or paused).</summary>
    AlreadyInLibrary = 2,

    /// <summary>
    /// A download to the same destination exists but is Failed or Expired (TASK-230) — nothing is claiming
    /// the destination, but a previous attempt at this exact file already exists in the library. Distinct
    /// from <see cref="AlreadyInLibrary"/> because the right response differs: renaming around a dead entry
    /// just hides it, so the caller should surface it rather than auto-rename past it.
    /// </summary>
    RecoverableInLibrary = 3,
}

/// <summary>
/// The result of a pre-download duplicate check (TASK-139): the kind of collision and, for an on-disk file,
/// its size and whether that size matches the expected download size (a strong duplicate signal). For
/// <see cref="DuplicateKind.AlreadyInLibrary"/> or <see cref="DuplicateKind.RecoverableInLibrary"/>,
/// <see cref="ExistingDownloadId"/> identifies the colliding row.
/// </summary>
public sealed record DuplicateCheckResult(
    DuplicateKind Kind,
    long? ExistingSizeOnDisk = null,
    bool SizeMatches = false,
    long? ExistingDownloadId = null)
{
    public static DuplicateCheckResult None { get; } = new(DuplicateKind.None);

    public bool IsDuplicate => Kind != DuplicateKind.None;
}

/// <summary>
/// Checks, before a download is queued, whether the target file already exists on disk or a download to the
/// same destination is already in the library (TASK-139), so the UI can warn and let the user skip or rename
/// rather than silently re-downloading or overwriting.
/// </summary>
public interface IDuplicateDownloadCheck
{
    Task<DuplicateCheckResult> CheckAsync(
        string directory, string fileName, long? expectedSize, CancellationToken cancellationToken = default);
}

internal sealed class DuplicateDownloadCheck : IDuplicateDownloadCheck
{
    private readonly IDownloadRepository _repository;

    public DuplicateDownloadCheck(IDownloadRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<DuplicateCheckResult> CheckAsync(
        string directory, string fileName, long? expectedSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return DuplicateCheckResult.None;
        }

        string path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            long? size = TryGetLength(path);
            bool sizeMatches = expectedSize is > 0 && size == expectedSize;
            return new DuplicateCheckResult(DuplicateKind.FileExistsOnDisk, size, sizeMatches);
        }

        // Only an active (still-claiming-the-destination) record counts as a collision here — a completed
        // record whose file the user has since deleted has nothing left to conflict with (user-reported: a
        // deleted file still warned "already downloaded"). FileExistsOnDisk above already covers the case
        // where the file is genuinely still present.
        IReadOnlyList<Download> existing = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        bool SamePath(Download d) =>
            string.Equals(d.Directory, directory, PathComparison) && string.Equals(d.Filename, fileName, PathComparison);

        if (existing.FirstOrDefault(d => IsActiveStatus(d.Status) && SamePath(d)) is { } active)
        {
            return new DuplicateCheckResult(DuplicateKind.AlreadyInLibrary, ExistingDownloadId: active.Id);
        }

        // Nothing is claiming the destination, but a previous attempt at this exact file is still sitting in
        // the library (TASK-230) — worth surfacing distinctly rather than silently letting a second,
        // unrelated row get created for the same file.
        if (existing.FirstOrDefault(d => IsRecoverableStatus(d.Status) && SamePath(d)) is { } recoverable)
        {
            return new DuplicateCheckResult(DuplicateKind.RecoverableInLibrary, ExistingDownloadId: recoverable.Id);
        }

        return DuplicateCheckResult.None;
    }

    private static bool IsActiveStatus(string status) =>
        status is DownloadStatusCodes.Queued or DownloadStatusCodes.Active or DownloadStatusCodes.Paused;

    private static bool IsRecoverableStatus(string status) =>
        status is DownloadStatusCodes.Failed or DownloadStatusCodes.Expired;

    private static long? TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null; // raced with a delete/rename — treat the size as unknown
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Windows/macOS paths are case-insensitive; Linux is case-sensitive.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
