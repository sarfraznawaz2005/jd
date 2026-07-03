using System.Text;
using FluentAssertions;
using JustDownload.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JustDownload.Tests.Logging;

/// <summary>
/// The internal error log (TASK-179): before this, nothing was registered as an <see cref="ILoggerProvider"/>
/// at all, so even <see cref="JustDownload.Core.Diagnostics.IGlobalErrorHandler"/>'s Critical-level captures
/// were discarded. This is a narrow, internal diagnostic trail — Error/Critical only, one file, no Settings
/// UI — not a general-purpose verbose logger.
/// </summary>
public sealed class ErrorLogFileProviderTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "jd-errorlog-" + Guid.NewGuid().ToString("N"), "errors.log");

    [Theory]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.Warning, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Trace, false)]
    public void IsEnabled_OnlyErrorAndCritical(LogLevel level, bool expected)
    {
        using var provider = new ErrorLogFileProvider(_path);
        ILogger logger = provider.CreateLogger("Test");

        logger.IsEnabled(level).Should().Be(expected);
    }

    [Fact]
    public void LoggingAnError_WritesToTheFile()
    {
        using var provider = new ErrorLogFileProvider(_path);
        ILogger logger = provider.CreateLogger("JustDownload.Core.Downloading.SegmentedDownloader");

        logger.LogError(new InvalidOperationException("boom"), "Segment {SegmentId} failed", 3);

        File.Exists(_path).Should().BeTrue();
        string content = File.ReadAllText(_path);
        content.Should().Contain("[ERROR]");
        content.Should().Contain("SegmentedDownloader");
        content.Should().Contain("Segment 3 failed");
        content.Should().Contain("InvalidOperationException");
        content.Should().Contain("boom");
    }

    [Fact]
    public void LoggingACriticalError_IsLabeledCrit()
    {
        using var provider = new ErrorLogFileProvider(_path);
        provider.CreateLogger("Test").LogCritical("unrecoverable");

        File.ReadAllText(_path).Should().Contain("[CRIT]").And.Contain("unrecoverable");
    }

    [Fact]
    public void LoggingAWarning_WritesNothing()
    {
        using var provider = new ErrorLogFileProvider(_path);
        provider.CreateLogger("Test").LogWarning("just a warning, not an error");

        File.Exists(_path).Should().BeFalse();
    }

    [Fact]
    public void FileExceedingTheSizeCap_RotatesToOldInsteadOfGrowingUnbounded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, new string('x', 5 * 1024 * 1024 + 1), Encoding.UTF8);

        using var provider = new ErrorLogFileProvider(_path);
        provider.CreateLogger("Test").LogError("triggers rotation");

        File.Exists(_path + ".old").Should().BeTrue("the oversized file was rotated out of the way");
        string rotated = File.ReadAllText(_path + ".old");
        rotated.Should().Contain(new string('x', 100), "the oversized content moved to .old rather than being discarded");
        File.ReadAllText(_path).Should().Contain("triggers rotation");
    }

    public void Dispose()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
