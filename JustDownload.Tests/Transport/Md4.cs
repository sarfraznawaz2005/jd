namespace JustDownload.Tests.Transport;

/// <summary>
/// MD4 (RFC 1320), needed only because NTLMv2's NT hash is defined as MD4 of the UTF-16LE password and the
/// BCL dropped MD4 as a named algorithm. Test-only fixture code for <see cref="NtlmMessages"/> — never used
/// by the shipped product.
/// </summary>
internal static class Md4
{
    public static byte[] Hash(byte[] message)
    {
        byte[] padded = Pad(message);

        uint a = 0x67452301;
        uint b = 0xefcdab89;
        uint c = 0x98badcfe;
        uint d = 0x10325476;

        var x = new uint[16];
        for (int block = 0; block < padded.Length; block += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                x[i] = BitConverter.ToUInt32(padded, block + (i * 4));
            }

            uint aa = a, bb = b, cc = c, dd = d;

            // Round 1: [a b c d k s] means a = (a + F(b,c,d) + X[k]) <<< s, in word order 0..15.
            for (int k = 0; k < 16; k += 4)
            {
                a = RotateLeft(a + F(b, c, d) + x[k + 0], 3);
                d = RotateLeft(d + F(a, b, c) + x[k + 1], 7);
                c = RotateLeft(c + F(d, a, b) + x[k + 2], 11);
                b = RotateLeft(b + F(c, d, a) + x[k + 3], 19);
            }

            // Round 2: same shape, word order {0,4,8,12, 1,5,9,13, 2,6,10,14, 3,7,11,15}, + 0x5A827999.
            int[] r2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
            for (int i = 0; i < 16; i += 4)
            {
                a = RotateLeft(a + G(b, c, d) + x[r2[i + 0]] + 0x5A827999, 3);
                d = RotateLeft(d + G(a, b, c) + x[r2[i + 1]] + 0x5A827999, 5);
                c = RotateLeft(c + G(d, a, b) + x[r2[i + 2]] + 0x5A827999, 9);
                b = RotateLeft(b + G(c, d, a) + x[r2[i + 3]] + 0x5A827999, 13);
            }

            // Round 3: word order {0,8,4,12, 2,10,6,14, 1,9,5,13, 3,11,7,15}, + 0x6ED9EBA1.
            int[] r3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
            for (int i = 0; i < 16; i += 4)
            {
                a = RotateLeft(a + H(b, c, d) + x[r3[i + 0]] + 0x6ED9EBA1, 3);
                d = RotateLeft(d + H(a, b, c) + x[r3[i + 1]] + 0x6ED9EBA1, 9);
                c = RotateLeft(c + H(d, a, b) + x[r3[i + 2]] + 0x6ED9EBA1, 11);
                b = RotateLeft(b + H(c, d, a) + x[r3[i + 3]] + 0x6ED9EBA1, 15);
            }

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var result = new byte[16];
        BitConverter.GetBytes(a).CopyTo(result, 0);
        BitConverter.GetBytes(b).CopyTo(result, 4);
        BitConverter.GetBytes(c).CopyTo(result, 8);
        BitConverter.GetBytes(d).CopyTo(result, 12);
        return result;
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static byte[] Pad(byte[] message)
    {
        long bitLength = (long)message.Length * 8;
        int paddedLength = ((message.Length + 8) / 64 * 64) + 64;
        var padded = new byte[paddedLength];
        message.CopyTo(padded, 0);
        padded[message.Length] = 0x80;
        BitConverter.GetBytes(bitLength).CopyTo(padded, paddedLength - 8);
        return padded;
    }
}
