using Avalonia.Styling;
using FluentAssertions;
using JustDownload.App.Services;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the pure theme cycling/mapping (TASK-047 AC1).</summary>
public sealed class ThemeCycleTests
{
    [Theory]
    [InlineData(ThemeMode.Light, ThemeMode.Dark)]
    [InlineData(ThemeMode.Dark, ThemeMode.System)]
    [InlineData(ThemeMode.System, ThemeMode.Light)]
    public void Next_CyclesLightDarkSystem(ThemeMode from, ThemeMode expected)
    {
        ThemeCycle.Next(from).Should().Be(expected);
    }

    [Fact]
    public void ToVariant_MapsModes()
    {
        ThemeCycle.ToVariant(ThemeMode.Light).Should().Be(ThemeVariant.Light);
        ThemeCycle.ToVariant(ThemeMode.Dark).Should().Be(ThemeVariant.Dark);
        ThemeCycle.ToVariant(ThemeMode.System).Should().Be(ThemeVariant.Default);
    }
}
