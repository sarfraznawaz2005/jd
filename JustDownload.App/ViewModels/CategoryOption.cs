using JustDownload.Core.Categorization;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One choice in the New URL dialog's category picker (TASK-053): either a concrete
/// <see cref="FileCategory"/> or the "Auto" option (<see cref="Category"/> is <see langword="null"/>) which
/// lets the detected category stand. Kept as a small record so the picker is data-bound and testable.
/// </summary>
public sealed record CategoryOption(string Label, FileCategory? Category)
{
    /// <summary>Whether this is the "Auto-detect" option rather than a fixed category.</summary>
    public bool IsAuto => Category is null;

    public override string ToString() => Label;
}
