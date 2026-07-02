namespace JustDownload.App.Services;

/// <summary>
/// Linux launch-at-login via an XDG desktop autostart entry under <c>~/.config/autostart</c>
/// (TASK-155, freedesktop.org autostart spec — honored by GNOME, KDE, XFCE, etc.). No admin rights,
/// removed cleanly on disable. The target directory, entry name, and the command to register are
/// injectable so the behaviour can be tested against an isolated directory without touching the real
/// autostart folder. On a non-Linux OS <see cref="IsSupported"/> is <see langword="false"/>, but the
/// file operations themselves are plain I/O and work on any OS (exercised for real by the test suite
/// on Windows).
/// </summary>
public sealed class LinuxAutostartService : IAutostartService
{
    private const string DefaultName = "justdownload";
    private const string ProductName = "JustDownload";

    private readonly string _directory;
    private readonly string _name;
    private readonly Func<string?> _command;

    public LinuxAutostartService(string? directory = null, string? name = null, Func<string?>? command = null)
    {
        _directory = directory ?? DefaultAutostartDirectory();
        _name = name ?? DefaultName;
        _command = command ?? (static () => Environment.ProcessPath);
    }

    public bool IsSupported => OperatingSystem.IsLinux();

    public bool IsEnabled() => File.Exists(DesktopEntryPath);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string? command = _command();
            if (string.IsNullOrEmpty(command))
            {
                return;
            }

            Directory.CreateDirectory(_directory);
            File.WriteAllText(DesktopEntryPath, BuildDesktopEntry(command));
        }
        else if (File.Exists(DesktopEntryPath))
        {
            File.Delete(DesktopEntryPath);
        }
    }

    private string DesktopEntryPath => Path.Combine(_directory, $"{_name}.desktop");

    private static string BuildDesktopEntry(string command)
    {
        string exec = command.Contains(' ') ? $"\"{command}\"" : command; // quote a path containing spaces
        return $"""
            [Desktop Entry]
            Type=Application
            Name={ProductName}
            Exec={exec}
            X-GNOME-Autostart-enabled=true
            Hidden=false
            """;
    }

    // Respects $XDG_CONFIG_HOME per the freedesktop.org base-directory spec, falling back to ~/.config.
    private static string DefaultAutostartDirectory()
    {
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
        return Path.Combine(configHome, "autostart");
    }
}
