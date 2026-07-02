using CommunityToolkit.Mvvm.Input;

namespace JustDownload.App.ViewModels;

/// <summary>The user's choice on the one-time ToS notice (TASK-160, docs/LEGAL.md).</summary>
public enum TosNoticeResult
{
    /// <summary>"Cancel" — the pending media download must not proceed.</summary>
    Cancel,

    /// <summary>"I understand — continue" — proceed with this download only.</summary>
    Continue,

    /// <summary>"Don't show this again" — proceed with this download and suppress future prompts.</summary>
    ContinueAndSuppress,
}

/// <summary>
/// The one-time "may violate site ToS" notice shown before the first media download (TASK-160). The copy
/// below is sourced verbatim from docs/LEGAL.md — do not paraphrase it, it is deliberate legal-notice
/// wording. The view binds these properties directly; this view-model has no dependencies of its own so the
/// gate that decides whether to show it at all (<see cref="Services.ITosNoticeGate"/>) stays unit-testable.
/// </summary>
public sealed partial class TosNoticeViewModel : ViewModelBase
{
    public string Heading { get; } = "Before you download media";

    public string Intro { get; } =
        "JustDownload can download video and audio that it detects on web pages. Downloading content from "
        + "some websites may violate that site's Terms of Service, and some content may be protected by "
        + "copyright.";

    public IReadOnlyList<string> Bullets { get; } =
    [
        "You are responsible for ensuring you have the right to download and use any content.",
        "JustDownload only downloads streams that are openly accessible. It does not bypass or remove any "
            + "DRM or copy protection, and it will not attempt to do so.",
        "JustDownload is not affiliated with, endorsed by, or sponsored by any website you download from.",
    ];

    public string Confirmation { get; } =
        "By continuing, you confirm that you understand this and will use JustDownload responsibly.";

    /// <summary>Raised once the user picks one of the three actions.</summary>
    public event EventHandler<TosNoticeResult>? CloseRequested;

    [RelayCommand]
    private void Continue() => CloseRequested?.Invoke(this, TosNoticeResult.Continue);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, TosNoticeResult.Cancel);

    [RelayCommand]
    private void DontShowAgain() => CloseRequested?.Invoke(this, TosNoticeResult.ContinueAndSuppress);
}
