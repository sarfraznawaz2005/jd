using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core.Integrity;
using Xunit;

namespace JustDownload.Tests.Integrity;

/// <summary>
/// The post-download integrity check (TASK-132): a completed file is verified against an MD5 or SHA-256 hex
/// digest, with the algorithm inferred from length, case-insensitive comparison, whitespace tolerated, and
/// clear outcomes for an unrecognised hash or a missing file.
/// </summary>
public sealed class ChecksumVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jd-checksum-" + Guid.NewGuid().ToString("N"));
    private readonly ChecksumVerifier _verifier = new();

    public ChecksumVerifierTests() => Directory.CreateDirectory(_dir);

    private string WriteFile(byte[] content)
    {
        string path = Path.Combine(_dir, "file.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // MD5 here only reproduces a publisher-style digest to test corruption detection — not a security use.
#pragma warning disable CA5351
    private static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
#pragma warning restore CA5351

    [Fact]
    public async Task Sha256_MatchingHash_ReturnsMatch()
    {
        byte[] body = RandomNumberGenerator.GetBytes(200_000); // spans many buffer reads
        string path = WriteFile(body);

        ChecksumResult result = await _verifier.VerifyAsync(path, Sha256Hex(body));

        result.Outcome.Should().Be(ChecksumOutcome.Match);
        result.IsMatch.Should().BeTrue();
        result.ComputedHash.Should().Be(Sha256Hex(body));
    }

    [Fact]
    public async Task Md5_MatchingHash_ReturnsMatch()
    {
        byte[] body = RandomNumberGenerator.GetBytes(50_000);
        string path = WriteFile(body);

        ChecksumResult result = await _verifier.VerifyAsync(path, Md5Hex(body));

        result.Outcome.Should().Be(ChecksumOutcome.Match);
    }

    [Fact]
    public async Task WrongHash_ReturnsMismatch_WithComputedHash()
    {
        byte[] body = RandomNumberGenerator.GetBytes(1000);
        string path = WriteFile(body);
        string wrong = new('a', 64);

        ChecksumResult result = await _verifier.VerifyAsync(path, wrong);

        result.Outcome.Should().Be(ChecksumOutcome.Mismatch);
        result.ComputedHash.Should().Be(Sha256Hex(body));
    }

    [Fact]
    public async Task Hash_IsCaseInsensitive_AndWhitespaceTolerant()
    {
        byte[] body = RandomNumberGenerator.GetBytes(1000);
        string path = WriteFile(body);
        string upperPadded = "  " + Sha256Hex(body).ToUpperInvariant() + "\n";

        ChecksumResult result = await _verifier.VerifyAsync(path, upperPadded);

        result.Outcome.Should().Be(ChecksumOutcome.Match);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("abc123")]            // too short for MD5
    [InlineData("zz" + "00000000000000000000000000000")] // 32 chars but non-hex
    public async Task UnrecognizedHashFormat_ReturnsUnrecognized(string badHash)
    {
        string path = WriteFile(new byte[] { 1, 2, 3 });

        ChecksumResult result = await _verifier.VerifyAsync(path, badHash);

        result.Outcome.Should().Be(ChecksumOutcome.UnrecognizedHashFormat);
        result.ComputedHash.Should().BeNull("no hashing is done for an unparseable expected hash");
    }

    [Fact]
    public async Task MissingFile_ReturnsFileNotFound()
    {
        string path = Path.Combine(_dir, "does-not-exist.bin");

        ChecksumResult result = await _verifier.VerifyAsync(path, new string('a', 64));

        result.Outcome.Should().Be(ChecksumOutcome.FileNotFound);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
