namespace JustDownload.Core.Updates;

/// <summary>
/// Configuration for the opt-in GitHub Releases update check (TASK-080). <see cref="ApiBaseUri"/> is
/// overridable so tests can point the checker at a loopback fixture instead of the real GitHub API.
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>The GitHub REST API base URL. Defaults to the real API; tests substitute a loopback base.</summary>
    public Uri ApiBaseUri { get; set; } = new("https://api.github.com/");

    /// <summary>The GitHub repository owner that publishes releases.</summary>
    public string RepositoryOwner { get; set; } = "sarfraznawaz2005";

    /// <summary>The GitHub repository name that publishes releases.</summary>
    public string RepositoryName { get; set; } = "jd";

    /// <summary>
    /// Base64 DER SubjectPublicKeyInfo of the ECDSA P-256 key release signatures are verified against.
    /// Defaults to the production placeholder (<see cref="UpdateSigningKey.ProductionPublicKeyBase64"/>);
    /// tests override this with a test-only keypair — never a production key.
    /// </summary>
    public string PublicKeyBase64 { get; set; } = UpdateSigningKey.ProductionPublicKeyBase64;

    /// <summary>
    /// An optional directory the verified installer is downloaded into, overriding the default per-user
    /// app-data location. <see langword="null"/> (the default) resolves to the standard location; tests
    /// substitute a temp directory so nothing is written under the real user profile.
    /// </summary>
    public string? VendorDirectory { get; set; }
}
