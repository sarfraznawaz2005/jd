using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IFileRevealer"/>: launches the platform file manager / default handler as a detached
/// child process. Windows uses <c>explorer /select</c>, macOS uses <c>open -R</c>, and Linux falls back to
/// <c>xdg-open</c> on the containing directory. Every launch is guarded so a missing file or absent helper
/// degrades quietly instead of throwing onto the UI thread (§1 "handle gracefully in release").
/// </summary>
public sealed class FileRevealer : IFileRevealer
{
    /// <inheritdoc />
    public void OpenFile(string? path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Launch(path!, useShellExecute: true);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Launch("open", path!);
        }
        else
        {
            Launch("xdg-open", path!);
        }
    }

    /// <inheritdoc />
    public void RevealInFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (File.Exists(path))
            {
                // explorer.exe needs the comma-joined /select form verbatim, so pass raw arguments rather
                // than the quoted ArgumentList (which it does not parse).
                TryStart(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false,
                });
            }
            else
            {
                OpenDirectoryOf(path);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (File.Exists(path))
            {
                Launch("open", "-R", path);
            }
            else
            {
                OpenDirectoryOf(path);
            }
        }
        else
        {
            OpenDirectoryOf(path);
        }
    }

    private static void OpenDirectoryOf(string path)
    {
        string? directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Launch(directory, useShellExecute: true);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Launch("open", directory);
        }
        else
        {
            Launch("xdg-open", directory);
        }
    }

    private static void Launch(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        TryStart(startInfo);
    }

    private static void Launch(string fileName, bool useShellExecute)
    {
        TryStart(new ProcessStartInfo { FileName = fileName, UseShellExecute = useShellExecute });
    }

    private static void TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            using Process? process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No file manager / handler available — nothing to surface; the action simply does nothing.
        }
    }
}
