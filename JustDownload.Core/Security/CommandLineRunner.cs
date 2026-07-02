using System.Diagnostics;

namespace JustDownload.Core.Security;

/// <summary>
/// Minimal async runner for invoking an OS keychain helper (<c>secret-tool</c> on Linux) as a
/// separate child process. Using the OS tool as its own binary — rather than P/Invoking a native
/// library — keeps the engine free of any non-permissive native dependency (CLAUDE.md §4: libsecret
/// is LGPL, fine as a separate process). The child is always torn down, including on cancellation, so
/// no helper process is orphaned (CLAUDE.md §2.5).
/// </summary>
internal static class CommandLineRunner
{
    internal readonly record struct Result(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, optionally piping
    /// <paramref name="standardInput"/> to its stdin, and returns the captured streams and exit code.
    /// Arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/> (no shell), so values
    /// are never re-parsed by a shell. Secrets pass through stdin where the tool supports it, keeping
    /// them out of the process command line (and thus out of <c>ps</c> output).
    /// </summary>
    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return new Result(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill — nothing to clean up.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Best-effort teardown; the OS will reap a helper that we could not signal.
        }
    }
}
