using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The "Sign in to YouTube" consent gate: skips the dialog once acknowledged, persists the acknowledgment
/// only on Continue, and never persists on Cancel. Mirrors <see cref="TosNoticeGateTests"/>.
/// </summary>
public sealed class YouTubeSignInConsentGateTests
{
    private static ISettingsService SettingsWith(bool acknowledged)
    {
        var settings = Substitute.For<ISettingsService>();
        var current = new AppSettings { YouTubeSignInConsentAcknowledged = acknowledged };
        settings.Current.Returns(current);
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Func<AppSettings, AppSettings>>()(current)));
        return settings;
    }

    [Fact]
    public async Task ConfirmAsync_AlreadyAcknowledged_SkipsDialog_AndReturnsTrue()
    {
        int shown = 0;
        var gate = new YouTubeSignInConsentGate(SettingsWith(acknowledged: true), _ =>
        {
            shown++;
            return Task.FromResult(YouTubeSignInConsentResult.Continue);
        });

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeTrue();
        shown.Should().Be(0, "a second sign-in attempt must not re-prompt once acknowledged");
    }

    [Fact]
    public async Task ConfirmAsync_Cancel_ReturnsFalse_AndDoesNotPersist()
    {
        ISettingsService settings = SettingsWith(acknowledged: false);
        var gate = new YouTubeSignInConsentGate(settings, _ => Task.FromResult(YouTubeSignInConsentResult.Cancel));

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeFalse();
        await settings.DidNotReceive().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_Continue_ReturnsTrue_AndPersistsAcknowledgment()
    {
        ISettingsService settings = SettingsWith(acknowledged: false);
        var gate = new YouTubeSignInConsentGate(settings, _ => Task.FromResult(YouTubeSignInConsentResult.Continue));

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeTrue();
        await settings.Received(1).UpdateAsync(
            Arg.Is<Func<AppSettings, AppSettings>>(m => m(new AppSettings()).YouTubeSignInConsentAcknowledged),
            Arg.Any<CancellationToken>());
    }
}
