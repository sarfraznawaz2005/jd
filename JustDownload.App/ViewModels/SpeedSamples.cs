using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One bar in a speed sparkline (TASK-137): its height in device-independent pixels
/// (0–<see cref="SpeedSamples.BarHeight"/>). Mutable and observable rather than an immutable record so a
/// tick can re-point an existing bar instead of replacing the collection — see <see cref="SpeedSamples"/>.
/// </summary>
public sealed partial class SpeedBar : ObservableObject
{
    [ObservableProperty]
    private double _height;

    public SpeedBar(double height) => _height = height;
}

/// <summary>
/// A fixed-length rolling history of speed samples (bytes/sec) backing a sparkline (TASK-137). Appending past
/// the capacity drops the oldest sample, so the series always shows the most recent window. Exposes the bars
/// as normalized pixel heights (relative to the window's peak) so the view binds rectangles without a
/// converter; an all-zero window renders flat. Pure and unit-testable — no timers or UI here.
/// </summary>
public sealed class SpeedSamples
{
    private readonly long[] _samples;
    private int _count;
    private int _head; // index of the oldest sample

    /// <param name="capacity">How many samples the window holds — also how many slots the chart divides.</param>
    /// <param name="barHeight">
    /// The pixel height a full-scale (peak) bar renders at. Per-instance rather than a constant because the
    /// detached progress window draws a taller, wider chart than the docked pane's card.
    /// </param>
    public SpeedSamples(int capacity = 60, double barHeight = 24)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(barHeight);
        _samples = new long[capacity];
        BarHeight = barHeight;
    }

    /// <summary>The pixel height a full-scale (peak) bar renders at; the chart card sizes itself to it.</summary>
    public double BarHeight { get; }

    public int Capacity => _samples.Length;

    public int Count => _count;

    /// <summary>The highest sample in the current window (0 when empty).</summary>
    public long Peak { get; private set; }

    /// <summary>The bars for binding, oldest-to-newest, as normalized pixel heights against the window peak.</summary>
    public ObservableCollection<SpeedBar> Bars { get; } = new();

    /// <summary>Appends a sample (clamped at 0), dropping the oldest when full, and refreshes the bars.</summary>
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

    /// <summary>
    /// Re-points the existing bars in place, appending only while the window is still filling. Clearing and
    /// refilling the collection instead would raise a Reset plus <see cref="Count"/> Adds every tick, and an
    /// ItemsControl answers that by tearing down and rebuilding every container — up to 120 of them per
    /// second per open chart, which the "light on slow systems" promise (D9) cannot afford. In steady state
    /// this raises no collection change at all: only the heights that actually moved notify.
    /// </summary>
    private void Rebuild()
    {
        long peak = 0;
        for (int i = 0; i < _count; i++)
        {
            peak = Math.Max(peak, _samples[(_head + i) % _samples.Length]);
        }

        Peak = peak;
        for (int i = 0; i < _count; i++)
        {
            long value = _samples[(_head + i) % _samples.Length];
            double height = peak == 0 ? 0 : (double)value / peak * BarHeight;
            if (i < Bars.Count)
            {
                Bars[i].Height = height; // ObservableProperty already suppresses a no-op assignment
            }
            else
            {
                Bars.Add(new SpeedBar(height));
            }
        }
    }
}
