namespace JustDownload.App.Services;

/// <summary>
/// macOS launch-at-login via a per-user LaunchAgent property list under
/// <c>~/Library/LaunchAgents</c> (TASK-155) — no admin rights, removed cleanly on disable. The target
/// directory, label, and the command to register are injectable so the behaviour can be tested against
/// an isolated directory without touching the real LaunchAgents folder. On a non-macOS OS
/// <see cref="IsSupported"/> is <see langword="false"/>, but the file operations themselves are plain
/// I/O and work on any OS (exercised for real by the test suite on Windows).
/// </summary>
public sealed class MacOsAutostartService : IAutostartService
{
    private const string DefaultLabel = "com.justdownload.app";

    private readonly string _directory;
    private readonly string _label;
    private readonly Func<string?> _command;

    public MacOsAutostartService(string? directory = null, string? label = null, Func<string?>? command = null)
    {
        _directory = directory ?? DefaultLaunchAgentsDirectory();
        _label = label ?? DefaultLabel;
        _command = command ?? (static () => Environment.ProcessPath);
    }

    public bool IsSupported => OperatingSystem.IsMacOS();

    public bool IsEnabled() => File.Exists(PlistPath);

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
            File.WriteAllText(PlistPath, BuildPlist(command));
        }
        else if (File.Exists(PlistPath))
        {
            File.Delete(PlistPath);
        }
    }

    private string PlistPath => Path.Combine(_directory, $"{_label}.plist");

    // Label must match the filename (minus extension) — macOS requires this. ProgramArguments is a
    // single-element array (no shell involved, so no quoting concerns).
    private string BuildPlist(string command) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{XmlEscape(_label)}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{XmlEscape(command)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>
        """;

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string DefaultLaunchAgentsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "LaunchAgents");
}
