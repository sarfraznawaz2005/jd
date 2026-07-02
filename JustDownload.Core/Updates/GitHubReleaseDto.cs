using System.Text.Json.Serialization;

namespace JustDownload.Core.Updates;

/// <summary>The subset of the GitHub "get the latest release" response the update checker needs.</summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto> Assets { get; set; } = [];
}

/// <summary>One release asset — enough to name it and locate its bytes.</summary>
internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
