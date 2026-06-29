using System.Globalization;

namespace JustDownload.Core.Media;

/// <summary>
/// Parses ffmpeg's <c>-progress</c> stream (TASK-040). ffmpeg emits one <c>key=value</c> per line and
/// terminates each block with a <c>progress=continue</c>/<c>progress=end</c> line; this reader
/// accumulates the keys of a block and yields a <see cref="FfmpegProgress"/> when the block closes. Pure
/// (no I/O), so the parsing is unit-testable on captured sample output.
/// </summary>
public sealed class FfmpegProgressReader
{
    private TimeSpan? _outTime;
    private double? _speed;
    private long? _totalSize;

    /// <summary>
    /// Feeds one line. Returns <see langword="true"/> and a <paramref name="progress"/> snapshot when the
    /// line closes a progress block (<c>progress=…</c>); otherwise updates internal state and returns
    /// <see langword="false"/>.
    /// </summary>
    public bool Push(string line, out FfmpegProgress progress)
    {
        progress = default;
        if (line is null)
        {
            return false;
        }

        int separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_us" or "out_time_ms":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros))
                {
                    _outTime = TimeSpan.FromMicroseconds(micros);
                }

                return false;

            case "out_time":
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed))
                {
                    _outTime = parsed;
                }

                return false;

            case "speed":
                _speed = ParseSpeed(value);
                return false;

            case "total_size":
                _totalSize = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
                    ? size
                    : null;
                return false;

            case "progress":
                progress = new FfmpegProgress(
                    _outTime ?? TimeSpan.Zero,
                    _speed,
                    _totalSize,
                    string.Equals(value, "end", StringComparison.Ordinal));
                Reset();
                return true;

            default:
                return false;
        }
    }

    private void Reset()
    {
        _outTime = null;
        _speed = null;
        _totalSize = null;
    }

    private static double? ParseSpeed(string value)
    {
        // e.g. "17.7x" → 17.7; "N/A" → null.
        string trimmed = value.TrimEnd('x', 'X').Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            ? speed
            : null;
    }
}
