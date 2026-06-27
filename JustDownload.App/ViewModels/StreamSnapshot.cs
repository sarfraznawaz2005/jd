using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One stream's worth of live connection stats fed to the segment visualization (TASK-055). A plain download
/// has a single stream ("File"); a muxed media download (separate video+audio, arriving with media
/// extraction) supplies one snapshot per stream, which the control renders as stacked strips.
/// </summary>
public sealed record StreamSnapshot(string Label, IReadOnlyList<ConnectionStat> Connections);
