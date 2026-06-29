using System.Runtime.Versioning;
using Microsoft.Win32;

namespace JustDownload.App.Services;

/// <summary>
/// Windows launch-at-login via the per-user <c>HKCU\…\CurrentVersion\Run</c> key (TASK-122) — no admin
/// rights, removed cleanly on disable. The registry sub-key, value name, and the command to register are
/// injectable so the behaviour can be tested against an isolated key without touching the real Run key. On a
/// non-Windows OS every member is inert (<see cref="IsSupported"/> is <see langword="false"/>).
/// </summary>
public sealed class WindowsAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "JustDownload";

    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly Func<string?> _command;

    public WindowsAutostartService(string? keyPath = null, string? valueName = null, Func<string?>? command = null)
    {
        _keyPath = keyPath ?? RunKeyPath;
        _valueName = valueName ?? DefaultValueName;
        _command = command ?? (static () => Environment.ProcessPath);
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled() => OperatingSystem.IsWindows() && ReadValue() is not null;

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (enabled)
        {
            string? command = _command();
            if (!string.IsNullOrEmpty(command))
            {
                WriteValue($"\"{command}\""); // quote the path so a space in it doesn't split the command
            }
        }
        else
        {
            DeleteValue();
        }
    }

    [SupportedOSPlatform("windows")]
    private object? ReadValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(_valueName);
    }

    [SupportedOSPlatform("windows")]
    private void WriteValue(string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(_valueName, value);
    }

    [SupportedOSPlatform("windows")]
    private void DeleteValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
