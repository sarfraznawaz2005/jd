namespace JustDownload.Core;

/// <summary>
/// The set of characters treated as invalid in a file/folder name, regardless of which OS is running
/// (TASK-173). <see cref="Path.GetInvalidFileNameChars"/> is OS-specific — on Unix it is only <c>NUL</c>
/// and <c>/</c>, so a name like <c>bad:name?.zip</c> is accepted there even though the same name is
/// invalid on Windows. JustDownload is cross-platform and a name chosen (or a file derived) on one OS may
/// later be moved to, synced to, or opened from another, so every name is validated/sanitized against the
/// union of what every supported OS forbids, not just the host OS's own rules.
/// </summary>
public static class CrossPlatformFileName
{
    /// <summary>Every character forbidden in a file/folder name on any OS this app supports.</summary>
    public static readonly char[] InvalidChars = BuildInvalidChars();

    private static char[] BuildInvalidChars()
    {
        var chars = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (char ch in "<>:\"/\\|?*")
        {
            chars.Add(ch);
        }

        for (char ch = '\0'; ch < ' '; ch++)
        {
            chars.Add(ch);
        }

        return [.. chars];
    }
}
