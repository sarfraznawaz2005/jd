namespace JustDownload.Core.Updates;

/// <summary>The outcome of an <see cref="IUpdateChecker"/> run (TASK-080).</summary>
public enum UpdateCheckStatus
{
    /// <summary>AC2: auto-update is off in Settings; no network call was made.</summary>
    Disabled,

    /// <summary>
    /// The production signing key is still the documented placeholder (<see cref="UpdateSigningKey"/>);
    /// fails closed — no network call was made either.
    /// </summary>
    NotConfigured,

    /// <summary>The installed version is already current with (or ahead of) the latest GitHub release.</summary>
    UpToDate,

    /// <summary>
    /// A newer release exists but this build can't verify/apply it automatically — no installer asset is
    /// published for this platform yet (today, anything other than Windows; TASK-077/078 haven't landed
    /// macOS/Linux packaging). The caller should point the user at <see cref="UpdateCheckResult.ReleaseUrl"/>.
    /// </summary>
    AvailableForManualDownload,

    /// <summary>A newer release was verified (signature + checksum) and its installer was launched.</summary>
    Applied,

    /// <summary>The release has no <c>checksums.txt.sig</c> asset — treated as unsigned and rejected.</summary>
    RejectedUnsigned,

    /// <summary>The signature over <c>checksums.txt</c> did not verify (corrupt bytes, or tampered content).</summary>
    RejectedInvalidSignature,

    /// <summary>
    /// The signed manifest didn't list this platform's asset, or the downloaded bytes didn't match the
    /// hash it lists.
    /// </summary>
    RejectedAssetHashMismatch,

    /// <summary>The check failed for an operational reason (network/parse error), not a security rejection.</summary>
    Error,
}
