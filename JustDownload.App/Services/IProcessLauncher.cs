using System.Diagnostics;

namespace JustDownload.App.Services;

/// <summary>
/// Launches an external program detached from the app (TASK-136). Abstracted so the post-download hook can be
/// unit-tested without spawning real processes.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>Starts <paramref name="executable"/> with the given <paramref name="arguments"/>, not waiting for exit.</summary>
    void Launch(string executable, IReadOnlyList<string> arguments);
}

/// <summary>
/// Default <see cref="IProcessLauncher"/>: starts the program with <see cref="ProcessStartInfo.ArgumentList"/>
/// (no shell), so each argument — including a file path with spaces or special characters — is passed exactly
/// and safely, with no command-line injection. The child runs independently; the app does not wait on it.
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public void Launch(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(startInfo);
        // The handle is disposed immediately; the child keeps running on its own (fire-and-forget hook).
    }
}
