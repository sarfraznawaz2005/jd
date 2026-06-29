using System.Collections.ObjectModel;

namespace JustDownload.App.ViewModels;

/// <summary>One bar in a speed sparkline (TASK-137): its height in device-independent pixels (0–<see cref="SpeedSamples.BarHeight"/>).</summary>
public sealed record SpeedBar(double Height);

/// <summary>
/// A fixed-length rolling history of speed samples (bytes/sec) backing a sparkline (TASK-137). Appending past
/// the capacity drops the oldest sample, so the series always shows the most recent window. Exposes the bars
/// as normalized pixel heights (relative to the window's peak) so the view binds rectangles without a
/// converter; an all-zero window renders flat. Pure and unit-testable — no timers or UI here.
/// </summary>
public sealed class SpeedSamples
{
    /// <summary>The pixel height a full-scale (peak) bar renders at.</summary>
    public const double BarHeight = 24;

    private readonly long[] _samples;
    private int _count;
    private int _head; // index of the oldest sample

    public SpeedSamples(int capacity = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new long[capacity];
    }

    public int Capacity => _samples.Length;

    public int Count => _count;

    /// <summary>The highest sample in the current window (0 when empty).</summary>
    public long Peak { get; private set; }

    /// <summary>The bars for binding, oldest-to-newest, as normalized pixel heights against the window peak.</summary>
    public ObservableCollection<SpeedBar> Bars { get; } = new();

    /// <summary>Appends a sample (clamped at 0), dropping the oldest when full, and rebuilds the bars.</summary>
    public void Add(long bytesPerSecond)
    {
        long sample = Math.Max(0, bytesPerSecond);
        if (_count < _samples.Length)
        {
            _samples[(_head + _count) % _samples.Length] = sample;
            _count++;
        }
        else
        {
            _samples[_head] = sample;
            _head = (_head + 1) % _samples.Length;
        }

        Rebuild();
    }

    /// <summary>Clears the history (e.g. when the detail panel selects a different download).</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
        Peak = 0;
        Bars.Clear();
    }

    private void Rebuild()
    {
        long peak = 0;
        for (int i = 0; i < _count; i++)
        {
            peak = Math.Max(peak, _samples[(_head + i) % _samples.Length]);
        }

        Peak = peak;
        Bars.Clear();
        for (int i = 0; i < _count; i++)
        {
            long value = _samples[(_head + i) % _samples.Length];
            double height = peak == 0 ? 0 : (double)value / peak * BarHeight;
            Bars.Add(new SpeedBar(height));
        }
    }
}
