namespace JustDownload.Core.Updates;

/// <summary>
/// The production ECDSA P-256 public key JustDownload verifies GitHub release signatures against
/// (TASK-080 key-custody decision). This is a documented PLACEHOLDER — empty until the maintainer
/// generates a real keypair offline (see <c>docs/release-signing.md</c>) and pastes the resulting public
/// key here (base64-encoded DER SubjectPublicKeyInfo, e.g. via <c>ECDsa.ExportSubjectPublicKeyInfo()</c>).
/// <para>
/// An empty/placeholder value is a deliberate fail-closed signal: <see cref="UpdateChecker"/> treats it as
/// "not configured" and never attempts to trust or download anything — never "the key looked empty so we
/// allowed it". NEVER put a real private key anywhere in this repository; only the public half ever
/// belongs here, and only once the maintainer has generated it themselves, offline.
/// </para>
/// </summary>
internal static class UpdateSigningKey
{
    /// <summary>Base64 DER SubjectPublicKeyInfo of the production signing key, or empty when unconfigured.</summary>
    public const string ProductionPublicKeyBase64 = "";
}
