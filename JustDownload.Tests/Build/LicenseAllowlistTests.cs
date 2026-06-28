using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace JustDownload.Tests.Build;

/// <summary>
/// Dependency license-allowlist gate (TASK-014, CLAUDE.md §4). Every <c>PackageReference</c> in the solution
/// must be vetted in <c>licenses.allowlist.json</c> with a permissive license; introducing a dependency that
/// is unlisted, carries a non-permissive (GPL/AGPL/commercial) license, or leaving a stale allowlist entry
/// fails this test — and therefore CI (AC0/AC2). The allowlist file is checked in at the repo root (AC1).
/// </summary>
[Trait("Category", "LicenseCheck")]
public sealed class LicenseAllowlistTests
{
    private sealed record Allowlist(string[] AllowedLicenses, Dictionary<string, string> Packages);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JustDownload.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must find the repo root (the folder with JustDownload.sln)");
        return dir!.FullName;
    }

    private static Allowlist LoadAllowlist(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "licenses.allowlist.json");
        File.Exists(path).Should().BeTrue("the license allowlist is checked in at the repo root (AC1)");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Allowlist>(stream, JsonOptions)!;
    }

    private static SortedSet<string> ReferencedPackages(string repoRoot)
    {
        var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string csproj in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            XDocument doc = XDocument.Load(csproj);
            foreach (XElement reference in doc.Descendants("PackageReference"))
            {
                string? id = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    packages.Add(id);
                }
            }
        }

        return packages;
    }

    [Fact]
    public void EveryReferencedPackage_IsVettedWithAPermissiveLicense()
    {
        string repoRoot = RepoRoot();
        Allowlist allowlist = LoadAllowlist(repoRoot);
        SortedSet<string> referenced = ReferencedPackages(repoRoot);

        referenced.Should().NotBeEmpty("the solution has package references to vet");

        var allowed = new HashSet<string>(allowlist.AllowedLicenses, StringComparer.OrdinalIgnoreCase);

        // AC0: a referenced package that isn't vetted in the allowlist fails the build.
        foreach (string package in referenced)
        {
            allowlist.Packages.Should().ContainKey(package,
                $"'{package}' must be added to licenses.allowlist.json with a permissive license before use (§4)");

            if (allowlist.Packages.TryGetValue(package, out string? license))
            {
                allowed.Should().Contain(license,
                    $"'{package}' is licensed '{license}', which is not in the permissive allowlist (§4)");
            }
        }
    }

    [Fact]
    public void Allowlist_HasNoStaleEntries()
    {
        string repoRoot = RepoRoot();
        Allowlist allowlist = LoadAllowlist(repoRoot);
        SortedSet<string> referenced = ReferencedPackages(repoRoot);

        foreach (string listed in allowlist.Packages.Keys)
        {
            referenced.Should().Contain(listed,
                $"'{listed}' is in the allowlist but no longer referenced — remove it to keep the allowlist honest");
        }
    }

    [Fact]
    public void AllowedLicenses_AreOnlyPermissive_NoCopyleftOrCommercial()
    {
        Allowlist allowlist = LoadAllowlist(RepoRoot());

        // A guard so copyleft/commercial can't be slipped into the allowed set itself.
        foreach (string license in allowlist.AllowedLicenses)
        {
            license.Should().NotContainAny("GPL", "AGPL", "LGPL", "Commercial", "Xceed",
                "the allowed-license set must stay permissive (§4)");
        }
    }
}
