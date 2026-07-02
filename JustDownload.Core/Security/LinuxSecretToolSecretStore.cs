using System.Runtime.Versioning;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Security;

/// <summary>
/// Linux <see cref="ISecretStore"/> backed by the freedesktop Secret Service (GNOME Keyring /
/// KWallet) via the <c>secret-tool</c> helper from libsecret. libsecret is LGPL, so it is invoked as
/// a separate process rather than linked (CLAUDE.md §4 — the same rule that lets us shell out to
/// ffmpeg). Secrets are keyed by the <c>service</c> attribute (product name) and an <c>account</c>
/// attribute holding the opaque reference.
/// </summary>
/// <remarks>
/// Implemented for parity and pending on-OS verification (this build is validated on Windows).
/// <c>secret-tool store</c> reads the secret from stdin, so the value never appears on the command
/// line. The <c>secret-tool</c> binary path is injectable (see <see cref="LinuxSecretToolSecretStore(IAppInfoProvider, string)"/>)
/// so a test can point it at a scripted stand-in and exercise the real process-spawn/stdin/stdout
/// logic without the real Secret Service daemon (TASK-113 AC0).
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretToolSecretStore : ISecretStore
{
    private const string SecretTool = "secret-tool";
    private const string ServiceAttribute = "service";
    private const string AccountAttribute = "account";

    private readonly string _service;
    private readonly string _secretTool;

    public LinuxSecretToolSecretStore(IAppInfoProvider appInfo, string secretToolPath = SecretTool)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentException.ThrowIfNullOrEmpty(secretToolPath);
        _service = appInfo.Name;
        _secretTool = secretToolPath;
    }

    public async Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string secretRef = SecretRef.New();
        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            _secretTool,
            [
                "store", "--label", $"{_service} credential",
                ServiceAttribute, _service, AccountAttribute, secretRef,
            ],
            standardInput: secret,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new SecretStoreException(
                $"Failed to store secret via secret-tool (exit {result.ExitCode}).");
        }

        return secretRef;
    }

    public async Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            _secretTool,
            ["lookup", ServiceAttribute, _service, AccountAttribute, secretRef],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        // secret-tool exits non-zero (1) with no output when the attribute set matches nothing.
        if (result.ExitCode != 0)
        {
            return null;
        }

        // 'secret-tool lookup' prints the secret verbatim with no trailing newline.
        return result.StandardOutput;
    }

    public async Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        // 'clear' is silently successful whether or not an item matched, so probe first to report
        // whether anything was actually removed.
        bool existed = await RetrieveAsync(secretRef, cancellationToken).ConfigureAwait(false) is not null;

        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            _secretTool,
            ["clear", ServiceAttribute, _service, AccountAttribute, secretRef],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new SecretStoreException(
                $"Failed to clear secret via secret-tool (exit {result.ExitCode}).");
        }

        return existed;
    }
}
