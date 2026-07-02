using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// Minimal [MS-NLMP] NTLMSSP message building/parsing for <see cref="LoopbackNtlmAuthServer"/> (TASK-110):
/// builds a TYPE_2 (Challenge) message carrying server-chosen target info, and parses + cryptographically
/// verifies a client's TYPE_3 (Authenticate) message's NTLMv2 response (NTProofStr = HMAC-MD5 keyed on the
/// password's NT hash, per spec section 3.3.2). Test-only: not part of the shipped product.
/// </summary>
internal static class NtlmMessages
{
    private const uint NegotiateUnicode = 0x00000001;
    private const uint RequestTarget = 0x00000004;
    private const uint NegotiateSign = 0x00000010;
    private const uint NegotiateNtlm = 0x00000200;
    private const uint NegotiateAlwaysSign = 0x00008000;
    private const uint NegotiateTargetTypeDomain = 0x00010000;
    private const uint NegotiateExtendedSessionSecurity = 0x00080000;
    private const uint NegotiateTargetInfo = 0x00800000;
    private const uint Negotiate128 = 0x20000000;
    private const uint NegotiateKeyExch = 0x40000000;
    private const uint Negotiate56 = 0x80000000;

    private const ushort AvNbComputerName = 1;
    private const ushort AvNbDomainName = 2;
    private const ushort AvEol = 0;

    public static byte[] BuildChallenge(byte[] serverChallenge, string targetDomain, string targetComputer)
    {
        byte[] targetName = Encoding.Unicode.GetBytes(targetDomain);
        byte[] targetInfo = BuildTargetInfo(targetDomain, targetComputer);

        const int headerLength = 48;
        var message = new byte[headerLength + targetName.Length + targetInfo.Length];

        Encoding.ASCII.GetBytes("NTLMSSP\0").CopyTo(message, 0);
        WriteUInt32(message, 8, 2); // MessageType = CHALLENGE_MESSAGE

        WriteField(message, 12, targetName.Length, headerLength);
        // .NET's HTTP client enforces ProtectionLevel.Sign for connection-based (NTLM/Negotiate) auth: if the
        // challenge doesn't grant every capability the client negotiated in its NEGOTIATE message (sign,
        // extended session security, key exchange, 128/56-bit), SSPI completes the handshake locally but
        // NegotiateAuthentication.GetOutgoingBlob comes back SecurityQosFailed and the AUTHENTICATE message
        // is never sent — silently, with no exception, just a 401. Granting the full set here avoids that.
        uint flags = NegotiateUnicode | RequestTarget | NegotiateSign | NegotiateNtlm | NegotiateAlwaysSign |
            NegotiateTargetTypeDomain | NegotiateExtendedSessionSecurity | NegotiateTargetInfo |
            Negotiate128 | NegotiateKeyExch | Negotiate56;
        WriteUInt32(message, 20, flags);
        serverChallenge.CopyTo(message, 24);
        // bytes 32..40 = Reserved, left zero.
        WriteField(message, 40, targetInfo.Length, headerLength + targetName.Length);

        targetName.CopyTo(message, headerLength);
        targetInfo.CopyTo(message, headerLength + targetName.Length);

        return message;
    }

    public static (string Domain, string Username, byte[] NtChallengeResponse) ParseAuthenticate(byte[] message)
    {
        (int ntLen, int ntOffset) = ReadField(message, 20);
        (int domainLen, int domainOffset) = ReadField(message, 28);
        (int userLen, int userOffset) = ReadField(message, 36);

        string domain = Encoding.Unicode.GetString(message, domainOffset, domainLen);
        string username = Encoding.Unicode.GetString(message, userOffset, userLen);
        byte[] ntResponse = message[ntOffset..(ntOffset + ntLen)];

        return (domain, username, ntResponse);
    }

    /// <summary>
    /// Verifies an NTLMv2 response: NTProofStr is the first 16 bytes, the rest is the client's "Temp" blob
    /// (timestamp, client challenge, echoed target info). Recomputes HMAC-MD5(ResponseKeyNT, ServerChallenge
    /// || Temp) and compares — a wrong password yields a different ResponseKeyNT and fails to match.
    /// </summary>
    public static bool VerifyNtlmV2(
        string domain, string username, string password, byte[] serverChallenge, byte[] ntChallengeResponse)
    {
        if (ntChallengeResponse.Length < 16)
        {
            return false;
        }

        byte[] proof = ntChallengeResponse[..16];
        byte[] temp = ntChallengeResponse[16..];

        byte[] responseKeyNt = Ntowfv2(password, username, domain);
        byte[] data = new byte[serverChallenge.Length + temp.Length];
        serverChallenge.CopyTo(data, 0);
        temp.CopyTo(data, serverChallenge.Length);

#pragma warning disable CA5351 // NTLMv2 (MS-NLMP) mandates HMAC-MD5; this is protocol interop, not a security choice.
        using var hmac = new HMACMD5(responseKeyNt);
#pragma warning restore CA5351
        byte[] expected = hmac.ComputeHash(data);

        return expected.AsSpan().SequenceEqual(proof);
    }

    private static byte[] Ntowfv2(string password, string username, string domain)
    {
        byte[] ntHash = Md4.Hash(Encoding.Unicode.GetBytes(password));
        string identity = username.ToUpperInvariant() + domain;
#pragma warning disable CA5351 // NTLMv2 (MS-NLMP) mandates HMAC-MD5; this is protocol interop, not a security choice.
        using var hmac = new HMACMD5(ntHash);
#pragma warning restore CA5351
        return hmac.ComputeHash(Encoding.Unicode.GetBytes(identity));
    }

    private static byte[] BuildTargetInfo(string domain, string computer)
    {
        using var buffer = new MemoryStream();
        WriteAvPair(buffer, AvNbDomainName, Encoding.Unicode.GetBytes(domain));
        WriteAvPair(buffer, AvNbComputerName, Encoding.Unicode.GetBytes(computer));
        WriteAvPair(buffer, AvEol, []);
        return buffer.ToArray();
    }

    private static void WriteAvPair(MemoryStream buffer, ushort id, byte[] value)
    {
        Span<byte> header = stackalloc byte[4];
        BitConverter.TryWriteBytes(header[..2], id);
        BitConverter.TryWriteBytes(header[2..], (ushort)value.Length);
        buffer.Write(header);
        buffer.Write(value);
    }

    /// <summary>Writes an [MS-NLMP] *_FIELDS descriptor (Len, MaxLen, Offset) at <paramref name="at"/>.</summary>
    private static void WriteField(byte[] message, int at, int length, int offset)
    {
        WriteUInt16(message, at, (ushort)length);
        WriteUInt16(message, at + 2, (ushort)length);
        WriteUInt32(message, at + 4, (uint)offset);
    }

    private static (int Length, int Offset) ReadField(byte[] message, int at) =>
        (BitConverter.ToUInt16(message, at), (int)BitConverter.ToUInt32(message, at + 4));

    private static void WriteUInt16(byte[] buffer, int at, ushort value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(at, 2), value);

    private static void WriteUInt32(byte[] buffer, int at, uint value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(at, 4), value);
}
