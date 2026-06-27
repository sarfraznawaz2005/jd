namespace JustDownload.Core.Categorization;

/// <summary>
/// The user-facing category a downloaded file is sorted into (PRD US-8 / §2.2). This is the complete,
/// closed set the product recognises — every file resolves to exactly one of these, with
/// <see cref="Other"/> as the catch-all so an unknown type is never unrepresentable. The category
/// drives list filtering and the default save-folder per type.
/// </summary>
public enum FileCategory
{
    /// <summary>Catch-all for files whose type is unknown or maps to no other category.</summary>
    Other = 0,

    /// <summary>Moving-image media (e.g. <c>.mp4</c>, <c>.mkv</c>, <c>video/*</c>).</summary>
    Video = 1,

    /// <summary>Sound media (e.g. <c>.mp3</c>, <c>.flac</c>, <c>audio/*</c>).</summary>
    Audio = 2,

    /// <summary>Documents and text (e.g. <c>.pdf</c>, <c>.docx</c>, <c>text/*</c>).</summary>
    Document = 3,

    /// <summary>Archives and disk images (e.g. <c>.zip</c>, <c>.7z</c>, <c>.iso</c>).</summary>
    Compressed = 4,

    /// <summary>Executables and installers (e.g. <c>.exe</c>, <c>.msi</c>, <c>.apk</c>).</summary>
    Program = 5,

    /// <summary>Still images (e.g. <c>.jpg</c>, <c>.png</c>, <c>image/*</c>).</summary>
    Image = 6,
}
