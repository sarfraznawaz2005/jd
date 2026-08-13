using JustDownload.Core.Data;

namespace JustDownload.LiveSmoke;

/// <summary>
/// Points the real DI-wired <c>IDatabasePathProvider</c> at a throwaway temp directory for this run, so
/// the harness never touches the real user's JustDownload database (same isolation approach as
/// JustDownload.Perf's TempDatabasePathProvider and JustDownload.Tests' TestEnvironment).
/// </summary>
internal sealed class SmokeDatabasePathProvider : IDatabasePathProvider
{
    public SmokeDatabasePathProvider(string directory)
    {
        DatabaseDirectory = directory;
        DatabasePath = Path.Combine(directory, "livesmoke.db");
    }

    public string DatabaseDirectory { get; }

    public string DatabasePath { get; }
}
