using FluentAssertions;
using JustDownload.Core.NativeMessaging;
using Xunit;

namespace JustDownload.Tests.NativeMessaging;

/// <summary>
/// TASK-182: both sides of the single-instance pipe — App's SingleInstanceCoordinator (the listening owner)
/// and Core's AppLauncher (a client, from the native host process) — resolve the pipe name through this
/// single shared function rather than duplicating the hashing logic, so they can never drift apart.
/// </summary>
public sealed class SingleInstancePipeNameTests
{
    [Fact]
    public void Resolve_IsDeterministic_ForTheSameName()
    {
        SingleInstancePipeName.Resolve("JustDownload.SingleInstance")
            .Should().Be(SingleInstancePipeName.Resolve("JustDownload.SingleInstance"));
    }

    [Fact]
    public void Resolve_DiffersForDifferentNames()
    {
        SingleInstancePipeName.Resolve("a").Should().NotBe(SingleInstancePipeName.Resolve("b"));
    }

    [Fact]
    public void Resolve_DefaultsToBaseName()
    {
        SingleInstancePipeName.Resolve().Should().Be(SingleInstancePipeName.Resolve(SingleInstancePipeName.BaseName));
    }

    [Fact]
    public void Resolve_Is16HexCharacters()
    {
        // The macOS/Linux Unix-domain-socket path-length limit is why this is hashed and truncated at all
        // (SingleInstanceCoordinator's doc comment) — a regression here would silently reintroduce that bug.
        string name = SingleInstancePipeName.Resolve("anything");
        name.Should().HaveLength(16);
        name.Should().MatchRegex("^[0-9A-F]{16}$");
    }

    [Fact]
    public void DrainInboxSignal_IsNeverAValidAbsoluteUri()
    {
        // App.axaml.cs's WireForwardedArguments must be able to tell this apart from a real forwarded
        // download URL using the same Uri.TryCreate check it already applies to arguments.
        Uri.TryCreate(SingleInstancePipeName.DrainInboxSignal, UriKind.Absolute, out _).Should().BeFalse();
    }
}
