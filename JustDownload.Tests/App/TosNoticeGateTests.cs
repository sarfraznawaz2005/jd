using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// The ToS-notice gate (TASK-160): skips the dialog once suppressed, persists suppression only on
/// "Don't show this again", and never persists on Cancel/Continue.
/// </summary>
public sealed class TosNoticeGateTests
{
    private static ISettingsService SettingsWith(bool suppressed)
    {
        var settings = Substitute.For<ISettingsService>();
        var current = new AppSettings { SuppressTosNotice = suppressed };
        settings.Current.Returns(current);
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Func<AppSettings, AppSettings>>()(current)));
        return settings;
    }

    [Fact]
    public async Task ConfirmAsync_AlreadySuppressed_SkipsDialog_AndReturnsTrue()
    {
        int shown = 0;
        var gate = new TosNoticeGate(SettingsWith(suppressed: true), _ =>
        {
            shown++;
            return Task.FromResult(TosNoticeResult.Continue);
        });

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeTrue();
        shown.Should().Be(0, "a second media download must not re-prompt once suppressed");
    }

    [Fact]
    public async Task ConfirmAsync_Cancel_ReturnsFalse_AndDoesNotPersist()
    {
        ISettingsService settings = SettingsWith(suppressed: false);
        var gate = new TosNoticeGate(settings, _ => Task.FromResult(TosNoticeResult.Cancel));

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeFalse();
        await settings.DidNotReceive().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_Continue_ReturnsTrue_AndDoesNotPersistSuppression()
    {
        ISettingsService settings = SettingsWith(suppressed: false);
        var gate = new TosNoticeGate(settings, _ => Task.FromResult(TosNoticeResult.Continue));

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeTrue();
        await settings.DidNotReceive().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_ContinueAndSuppress_ReturnsTrue_AndPersistsSuppression()
    {
        ISettingsService settings = SettingsWith(suppressed: false);
        var gate = new TosNoticeGate(settings, _ => Task.FromResult(TosNoticeResult.ContinueAndSuppress));

        bool proceed = await gate.ConfirmAsync();

        proceed.Should().BeTrue();
        await settings.Received(1).UpdateAsync(
            Arg.Is<Func<AppSettings, AppSettings>>(m => m(new AppSettings()).SuppressTosNotice),
            Arg.Any<CancellationToken>());
    }
}
