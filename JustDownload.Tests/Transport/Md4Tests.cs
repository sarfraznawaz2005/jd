using FluentAssertions;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>RFC 1320 Appendix A.5 test vectors, isolating the MD4 primitive from the NTLMv2 handshake that consumes it (TASK-110).</summary>
public sealed class Md4Tests
{
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    public void Hash_MatchesRfc1320Vectors(string input, string expectedHex)
    {
        byte[] hash = Md4.Hash(System.Text.Encoding.ASCII.GetBytes(input));

        Convert.ToHexString(hash).ToLowerInvariant().Should().Be(expectedHex);
    }
}
