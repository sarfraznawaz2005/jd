using FluentAssertions;
using JustDownload.Core.Logging;
using Xunit;

namespace JustDownload.Tests.Logging;

/// <summary>
/// Verifies the pure redaction layer (TASK-016, criterion [1]): secrets are masked, ordinary text
/// is left intact. These are the unit tests CLAUDE.md §5 / §3 call for around log redaction.
/// </summary>
public class SecretRedactorTests
{
    private readonly SecretRedactor _redactor = new();

    [Theory]
    [InlineData("Authorization: Bearer abcDEF123456ghiJKL789", "abcDEF123456ghiJKL789")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("{\"Authorization\":\"Bearer secrettokenvalue123\"}", "secrettokenvalue123")]
    [InlineData("GET /file?token=supersecretvalue123&x=1", "supersecretvalue123")]
    [InlineData("https://cdn.example.com/v.mp4?X-Amz-Signature=abc123def456ghi789", "abc123def456ghi789")]
    [InlineData("blob.mp4?sig=AaBbCcDd1122334455&se=2026-01-01", "AaBbCcDd1122334455")]
    [InlineData("password=hunter2longenough", "hunter2longenough")]
    [InlineData("{\"access_token\": \"abcdef123456ghijkl\"}", "abcdef123456ghijkl")]
    [InlineData("api_key: 9f8e7d6c5b4a3210zzzz", "9f8e7d6c5b4a3210zzzz")]
    [InlineData("Cookie: sessionId=AABBCCDDEEFF001122", "AABBCCDDEEFF001122")]
    [InlineData("Set-Cookie: token=deadbeefcafef00dbabe", "deadbeefcafef00dbabe")]
    public void Redact_MasksSecretValue_AndStripsTheRawSecret(string input, string secret)
    {
        string output = _redactor.Redact(input);

        output.Should().Contain(SecretRedactor.Mask);
        output.Should().NotContain(secret, $"the secret '{secret}' must be masked");
    }

    [Theory]
    [InlineData("Downloading https://example.com/movie.mp4 at 12 MB/s")]
    [InlineData("Segment 3 of 8 completed; resuming from offset 1048576")]
    [InlineData("The secret to fast downloads is parallel segments")]
    [InlineData("Bearer of bad news: the server returned 503")]
    [InlineData("")]
    public void Redact_LeavesNonSecretText_Unchanged(string input)
    {
        string output = _redactor.Redact(input);

        output.Should().NotContain(SecretRedactor.Mask);
        output.Should().Be(input);
    }

    [Fact]
    public void Redact_Null_ReturnsEmptyString()
    {
        _redactor.Redact(null).Should().BeEmpty();
    }

    [Fact]
    public void Redact_MasksEveryTokenParam_InAMultiSecretUrl()
    {
        const string url =
            "https://h.net/v.m3u8?token=AaaaaaaaaaBbbbbbbbbb&X-Amz-Signature=Ccccccccccdddddddddd&q=720";

        string output = _redactor.Redact(url);

        output.Should().NotContain("AaaaaaaaaaBbbbbbbbbb");
        output.Should().NotContain("Ccccccccccdddddddddd");
        output.Should().Contain("q=720", "non-secret query params stay readable");
    }
}
