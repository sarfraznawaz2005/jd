using Avalonia.Input.Platform;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IClipboardService"/>. The platform clipboard is reached through the active window's
/// <see cref="IClipboard"/>, which only exists once a top-level is shown — so it is supplied lazily through a
/// provider set during startup rather than captured in the constructor.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private readonly Func<IClipboard?> _clipboardProvider;

    public ClipboardService(Func<IClipboard?> clipboardProvider)
    {
        ArgumentNullException.ThrowIfNull(clipboardProvider);
        _clipboardProvider = clipboardProvider;
    }

    /// <inheritdoc />
    public string? LastCopiedText { get; private set; }

    /// <inheritdoc />
    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        IClipboard? clipboard = _clipboardProvider();
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
            LastCopiedText = text;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetTextAsync()
    {
        IClipboard? clipboard = _clipboardProvider();
        return clipboard is null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(false);
    }
}
