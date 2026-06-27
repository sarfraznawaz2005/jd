using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The live segment/connection visualization (TASK-055, US-15b): one stacked block-strip per stream, each
/// cell filling with a connection's progress, plus "Segments: N". To stay light on slow hardware the strip
/// repaints on a fixed timer capped at <see cref="RepaintHz"/> (≤ 4 Hz) — it pulls the latest snapshot on
/// each tick rather than redrawing on every progress event (which can fire far faster). Stream snapshots are
/// supplied by a provider so the same control serves a single-stream download today and muxed video+audio
/// (one strip each) when media extraction lands.
/// </summary>
public sealed partial class SegmentVisualizationViewModel : ViewModelBase, IDisposable
{
    /// <summary>The maximum repaint rate in hertz (AC3: capped at 4 Hz).</summary>
    public const int RepaintHz = 4;

    /// <summary>The repaint interval derived from <see cref="RepaintHz"/> (250 ms).</summary>
    public static readonly TimeSpan RepaintInterval = TimeSpan.FromMilliseconds(1000 / RepaintHz);

    private readonly Func<IReadOnlyList<StreamSnapshot>> _provider;
    private readonly Dictionary<string, StreamStripViewModel> _stripsByLabel = [];
    private DispatcherTimer? _timer;

    public SegmentVisualizationViewModel(Func<IReadOnlyList<StreamSnapshot>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <summary>The stacked stream strips (one for a plain download, one per stream for muxed media).</summary>
    public ObservableCollection<StreamStripViewModel> Streams { get; } = [];

    /// <summary>Whether the repaint timer is currently running.</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Whether there is anything to show.</summary>
    public bool HasStreams => Streams.Count > 0;

    /// <summary>Starts the capped-rate repaint loop and paints an immediate first frame.</summary>
    public void Start()
    {
        Refresh();
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = RepaintInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the repaint loop and clears the strips.</summary>
    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }

        Streams.Clear();
        _stripsByLabel.Clear();
        OnPropertyChanged(nameof(HasStreams));
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    /// <summary>Pulls the latest snapshot and reconciles the strips. Exposed for deterministic testing.</summary>
    public void Refresh() => Update(_provider());

    /// <summary>Reconciles the strips against the supplied stream snapshots (in place, no churn).</summary>
    public void Update(IReadOnlyList<StreamSnapshot> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);

        var seen = new HashSet<string>(streams.Count);
        for (int index = 0; index < streams.Count; index++)
        {
            StreamSnapshot snapshot = streams[index];
            string key = snapshot.Label;
            seen.Add(key);

            if (!_stripsByLabel.TryGetValue(key, out StreamStripViewModel? strip))
            {
                strip = new StreamStripViewModel(snapshot.Label);
                _stripsByLabel[key] = strip;
                Streams.Insert(Math.Min(index, Streams.Count), strip);
            }

            strip.Update(snapshot.Label, snapshot.Connections);
        }

        for (int i = Streams.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Streams[i].Label))
            {
                _stripsByLabel.Remove(Streams[i].Label);
                Streams.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(HasStreams));
    }

    public void Dispose() => Stop();
}
