using System.Security.Cryptography;

namespace JustDownload.Tests.Updates;

// TEST KEY ONLY — never trust this in production, generated for JustDownload.Tests fixtures (TASK-080
// key-custody decision). This ECDSA P-256 keypair protects nothing real: it exists solely to sign the fake
// `checksums.txt` served by PathRoutingLoopbackServer in UpdateCheckerTests, so signature verification is
// exercised with real cryptography rather than a mock. It is fine for this key to live in the repo.
internal static class TestSigningKeys
{
    /// <summary>Base64 PKCS8 private key (test-only). Used to sign the fake checksums.txt in tests.</summary>
    private const string PrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgJumLPxuVcP1ssCw24yfUVlnGolQqt0kpd4Nib3oaepyhRANCAARQ" +
        "iBgOkuu4kfDMmwIoI/5o/yczfSDkKvsen22iurqIhTCzZsQMSuNbU0Do/uBFVpCzBAgHf2TLFWPOjqd3f2nn";

    /// <summary>Base64 DER SubjectPublicKeyInfo (test-only). Fed into <c>UpdateOptions.PublicKeyBase64</c> in tests.</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEUIgYDpLruJHwzJsCKCP+aP8nM30g5Cr7Hp9torq6iIUws2bEDErjW1NA6P7gRVaQ" +
        "swQIB39kyxVjzo6nd39p5w==";

    /// <summary>Signs <paramref name="data"/> with the test private key (ECDSA P-256, SHA-256).</summary>
    public static byte[] Sign(byte[] data)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyPkcs8Base64), out _);
        return key.SignData(data, HashAlgorithmName.SHA256);
    }
}
