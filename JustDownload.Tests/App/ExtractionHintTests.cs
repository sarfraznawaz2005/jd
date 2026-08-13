using FluentAssertions;
using JustDownload.App.Formatting;
using JustDownload.Core.Media.Extraction;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for <see cref="ExtractionHintClassifier"/>: it maps a known failure reason onto an
/// actionable hint, and returns <see langword="null"/> for declines or unrelated failures (AC1).</summary>
public sealed class ExtractionHintTests
{
    [Fact]
    public void Classify_JsRuntimeReason_ReturnsInstallJsRuntime()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Declined("in-house"),
            MediaExtractionAttempt.Failed(
                "yt-dlp",
                "No supported JavaScript runtime could be found. YouTube extraction without a JS runtime has been deprecated"),
        };

        ExtractionHint? hint = ExtractionHintClassifier.Classify(attempts);

        hint.Should().NotBeNull();
        hint!.Kind.Should().Be(ExtractionHintKind.InstallJsRuntime);
        hint.ActionUri.Should().StartWith("https://deno.land/");
    }

    [Fact]
    public void Classify_BotChallengeReason_ReturnsTryCookies()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Failed("yt-dlp", "Sign in to confirm you're not a bot"),
        };

        ExtractionHint? hint = ExtractionHintClassifier.Classify(attempts);

        hint.Should().NotBeNull();
        hint!.Kind.Should().Be(ExtractionHintKind.TryCookies);
        hint.ActionUri.Should().Contain("yt-dlp").And.Contain("cookies");
    }

    [Fact]
    public void Classify_Http429Reason_ReturnsTryCookies()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Failed("yt-dlp", "429: Too Many Requests"),
        };

        ExtractionHint? hint = ExtractionHintClassifier.Classify(attempts);

        hint.Should().NotBeNull();
        hint!.Kind.Should().Be(ExtractionHintKind.TryCookies);
    }

    [Fact]
    public void Classify_CookiesReason_ReturnsTryCookies()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Failed("yt-dlp", "ERROR: Use --cookies cookies.txt to authenticate"),
        };

        ExtractionHint? hint = ExtractionHintClassifier.Classify(attempts);

        hint.Should().NotBeNull();
        hint!.Kind.Should().Be(ExtractionHintKind.TryCookies);
    }

    [Fact]
    public void Classify_JsRuntimeTakesPriorityOverBotCookies()
    {
        // If both signals somehow appear, the JS-runtime hint wins (it's the more actionable, install-once fix).
        var attempts = new[]
        {
            MediaExtractionAttempt.Failed("yt-dlp", "Sign in to confirm you're not a bot"),
            MediaExtractionAttempt.Failed("yt-dlp", "No supported JavaScript runtime could be found"),
        };

        ExtractionHint? hint = ExtractionHintClassifier.Classify(attempts);

        hint.Should().NotBeNull();
        hint!.Kind.Should().Be(ExtractionHintKind.InstallJsRuntime);
    }

    [Fact]
    public void Classify_AllDeclined_ReturnsNull()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Declined("hls"),
            MediaExtractionAttempt.Declined("progressive"),
        };

        ExtractionHintClassifier.Classify(attempts).Should().BeNull();
    }

    [Fact]
    public void Classify_UnrelatedFailureReason_ReturnsNull()
    {
        var attempts = new[]
        {
            MediaExtractionAttempt.Failed("hls", "HTTP 403 fetching https://video.twimg.com/pl/x.m3u8"),
        };

        ExtractionHintClassifier.Classify(attempts).Should().BeNull("an unrelated failure has no actionable hint");
    }

    [Fact]
    public void Classify_EmptyAttempts_ReturnsNull()
    {
        ExtractionHintClassifier.Classify([]).Should().BeNull();
    }
}
